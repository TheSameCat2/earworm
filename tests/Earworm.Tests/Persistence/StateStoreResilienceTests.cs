using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Earworm.Config;
using Earworm.Persistence;

namespace Earworm.Tests.Persistence;

public sealed class StateStoreResilienceTests
{
    private static (StateStore store, string dbPath) BuildStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"earworm_resilience_{Guid.NewGuid():N}.db");
        var config = new EarwormConfig
        {
            Persistence = new PersistenceConfig
            {
                SqlitePath = dbPath,
                HistoryRetentionCount = 100
            }
        };
        var store = new StateStore(config, NullLogger<StateStore>.Instance);
        return (store, dbPath);
    }

    private static void Cleanup(StateStore store, string dbPath)
    {
        store.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SubmitWriteAsync_AfterWorkerDeath_FaultsInsteadOfHanging()
    {
        var (store, dbPath) = BuildStore();
        try
        {
            // Force worker death by cancelling the internal CancellationTokenSource via
            // reflection. This simulates the scenario where ProcessWritesAsync exits the
            // await foreach unexpectedly (the contract under test in issue #8).
            var ctsField = typeof(StateStore).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic);
            ctsField.Should().NotBeNull("StateStore exposes an internal CTS we can cancel for the test");
            var cts = (CancellationTokenSource)ctsField!.GetValue(store)!;
            cts.Cancel();

            // Give the worker a beat to observe the cancel and run its cleanup path.
            var workerTaskField = typeof(StateStore).GetField("_writeWorkerTask", BindingFlags.Instance | BindingFlags.NonPublic);
            var workerTask = (Task)workerTaskField!.GetValue(store)!;
            (await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(2)))).Should().Be(workerTask, "worker should exit after cancel");
            store.IsWriterHealthy.Should().BeFalse();

            // After worker death the channel must be completed; the next submission
            // must fault quickly (not hang forever waiting on a dead TCS).
            var submit = store.SubmitWriteAsync(async _ => { await Task.CompletedTask; return 1; });
            var completed = await Task.WhenAny(submit, Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().Be(submit, "submission after worker death must not hang");

            Func<Task> awaitFaulted = async () => await submit;
            await awaitFaulted.Should().ThrowAsync<Exception>("submission should observe channel failure, not hang");
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    [Fact]
    public async Task ApplyDbScopedPragmas_LeavesJournalModeAsWal()
    {
        var (store, dbPath) = BuildStore();
        try
        {
            await using var conn = store.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await cmd.ExecuteScalarAsync();
            mode.Should().Be("wal");
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    [Fact]
    public async Task SubmitWriteAsync_WhenJobThrows_FaultsJobButKeepsWorkerAlive()
    {
        var (store, dbPath) = BuildStore();
        try
        {
            // First job throws inside its writeFunc — its TCS must fault, but the worker
            // must continue processing subsequent jobs.
            var bad = store.SubmitWriteAsync<int>(_ => throw new InvalidOperationException("boom"));
            Func<Task> awaitBad = async () => await bad;
            await awaitBad.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

            // Worker should still be healthy: a follow-up no-op write must complete.
            var good = store.SubmitWriteAsync(async _ => { await Task.CompletedTask; return 42; });
            var done = await Task.WhenAny(good, Task.Delay(TimeSpan.FromSeconds(2)));
            done.Should().Be(good, "worker must survive a single bad job");
            (await good).Should().Be(42);
            store.IsWriterHealthy.Should().BeTrue();
            store.PendingWriteCount.Should().Be(0);
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    [Fact]
    public async Task Dispose_DrainsAllAcceptedWrites_BeforeStoppingWorker()
    {
        var (store, dbPath) = BuildStore();
        try
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int executed = 0;

            var writes = Enumerable.Range(0, 20)
                .Select(i => store.SubmitWriteAsync(async _ =>
                {
                    if (i == 0)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task;
                    }
                    Interlocked.Increment(ref executed);
                }))
                .ToArray();

            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            store.PendingWriteCount.Should().Be(20);

            var disposeTask = Task.Run(store.Dispose);
            disposeTask.IsCompleted.Should().BeFalse("the first accepted write is still running");

            releaseFirst.TrySetResult();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(writes);

            executed.Should().Be(20);
            store.PendingWriteCount.Should().Be(0);
            store.IsWriterHealthy.Should().BeFalse();
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }
}
