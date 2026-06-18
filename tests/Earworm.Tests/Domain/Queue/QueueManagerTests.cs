using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Earworm.Config;
using Earworm.Domain.Queue;
using Earworm.Persistence;
using Earworm.Persistence.Repositories;
using Earworm.Persistence.Schema;

namespace Earworm.Tests.Domain.Queue;

public sealed class QueueManagerTests
{
    private static EarwormConfig BuildConfig() => new()
    {
        Persistence = new PersistenceConfig
        {
            SqlitePath = Path.Combine(Path.GetTempPath(), $"earworm_qmgr_{Guid.NewGuid():N}.db"),
            HistoryRetentionCount = 100,
        },
        // No queue caps — we want to exercise concurrency without rejection.
        Queue = new QueueConfig(),
    };

    private static async Task RunWithQueueManagerAsync(Func<QueueManager, IQueueRepository, Task> body, EarwormConfig? config = null)
    {
        config ??= BuildConfig();
        var stateStore = new StateStore(config, NullLogger<StateStore>.Instance);
        try
        {
            var migrator = new SchemaMigrator(stateStore.ConnectionString, NullLogger<SchemaMigrator>.Instance);
            migrator.Migrate();

            var queueRepo = new QueueRepository(stateStore);
            var snapshotRepo = new SnapshotRepository(stateStore);
            var manager = new QueueManager(queueRepo, snapshotRepo, config, NullLogger<QueueManager>.Instance, "g1");

            await body(manager, queueRepo);
        }
        finally
        {
            stateStore.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { File.Delete(config.Persistence.SqlitePath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task AddTrackAsync_ConcurrentCallers_AssignContiguousPositionsWithNoDuplicates()
    {
        await RunWithQueueManagerAsync(async (manager, queueRepo) =>
        {
            const int N = 50;
            var tasks = new List<Task>();
            for (int i = 0; i < N; i++)
            {
                int local = i;
                tasks.Add(Task.Run(() => manager.AddTrackAsync(
                    sourceType: "youtube",
                    sourceId: $"id_{local}",
                    title: $"Track {local}",
                    artist: "Artist",
                    durationSeconds: 60,
                    requestedByUserId: $"user_{local}",
                    requestedByDisplayName: $"User {local}",
                    guildId: "g1")));
            }

            await Task.WhenAll(tasks);

            // In-memory queue: positions must be exactly 0..N-1 with no duplicates.
            var inMemory = manager.GetQueue();
            inMemory.Should().HaveCount(N);
            inMemory.Select(q => q.Position).OrderBy(p => p).Should().Equal(Enumerable.Range(0, N));
            inMemory.Select(q => q.QueueItemId).Distinct().Should().HaveCount(N, "every row must have a unique queue_item_id");

            // DB must agree.
            var dbQueue = await queueRepo.GetQueueAsync("g1");
            dbQueue.Should().HaveCount(N);
            dbQueue.Select(q => q.Position).Should().Equal(Enumerable.Range(0, N));
        });
    }

    [Fact]
    public async Task DequeueAndRemove_Interleaved_LeaveQueueConsistent()
    {
        await RunWithQueueManagerAsync(async (manager, queueRepo) =>
        {
            const int N = 20;
            for (int i = 0; i < N; i++)
            {
                await manager.AddTrackAsync(
                    sourceType: "youtube",
                    sourceId: $"id_{i}",
                    title: $"Track {i}",
                    artist: "Artist",
                    durationSeconds: 60,
                    requestedByUserId: "u1",
                    requestedByDisplayName: "User",
                    guildId: "g1");
            }

            manager.Count.Should().Be(N);

            // Concurrently dequeue 5 from the front while removing the LAST item
            // 5 times (each remove uses the current last index resolved under-lock).
            var tasks = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(() => manager.DequeueAsync()));
            }
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        int lastIdx = manager.Count - 1;
                        if (lastIdx < 0) return;
                        try
                        {
                            await manager.RemoveTrackAsync(lastIdx, userId: "ignored", isDj: true);
                            return;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Lost the race to another remover; retry.
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // 10 items removed total: 5 from the front and 5 from the back.
            var remaining = manager.GetQueue();
            remaining.Should().HaveCount(N - 10);
            remaining.Select(q => q.Position).Should().Equal(Enumerable.Range(0, N - 10));
            remaining.Select(q => q.QueueItemId).Distinct().Should().HaveCount(N - 10);

            // DB matches in-memory state by identity and order.
            // Positions in the DB may have gaps after removals (lazy renumber);
            // QueueManager holds the authoritative dense in-memory positions.
            var dbQueue = await queueRepo.GetQueueAsync("g1");
            dbQueue.Select(q => q.QueueItemId).Should().Equal(remaining.Select(q => q.QueueItemId));
            dbQueue.Should().HaveCount(N - 10);
        });
    }

    [Fact]
    public async Task TrackQueued_HandlerThatCallsCount_DoesNotDeadlock()
    {
        await RunWithQueueManagerAsync(async (manager, _) =>
        {
            int observedCount = -1;
            var fired = new ManualResetEventSlim(false);
            manager.TrackQueued += item =>
            {
                // Handler synchronously calls into the manager. If events were
                // raised under _lock, this re-entry would deadlock.
                observedCount = manager.Count;
                var _snapshot = manager.GetQueue();
                fired.Set();
            };

            var addTask = manager.AddTrackAsync(
                sourceType: "youtube",
                sourceId: "x",
                title: "X",
                artist: "Y",
                durationSeconds: 60,
                requestedByUserId: "u",
                requestedByDisplayName: "U",
                guildId: "g1");

            var completed = await Task.WhenAny(addTask, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().BeSameAs(addTask, "AddTrackAsync must complete — re-entrant handler implies events were raised outside _lock");
            await addTask;

            fired.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("TrackQueued must have fired");
            observedCount.Should().Be(1);
        });
    }

    [Fact]
    public async Task TrackRemoved_HandlerThatCallsCount_DoesNotDeadlock()
    {
        await RunWithQueueManagerAsync(async (manager, _) =>
        {
            await manager.AddTrackAsync("youtube", "x", "X", "Y", 60, "u", "U", "g1");

            int observedCount = -1;
            manager.TrackRemoved += item =>
            {
                observedCount = manager.Count;
            };

            var removeTask = manager.RemoveTrackAsync(0, "u", isDj: true);
            var completed = await Task.WhenAny(removeTask, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().BeSameAs(removeTask, "RemoveTrackAsync must complete — re-entrant handler implies events were raised outside _lock");
            await removeTask;

            observedCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task RequeueFrontAsync_TrackQueuedEvent_PayloadAtPositionZero_AndQueueContainsItemAtIndexZero()
    {
        await RunWithQueueManagerAsync(async (manager, _) =>
        {
            // Seed the queue with 3 other tracks so position 0 is non-trivial.
            for (int i = 0; i < 3; i++)
            {
                await manager.AddTrackAsync("youtube", $"seed_{i}", $"Seed {i}", "Seeder", 60, "seeduser", "Seeder", "g1");
            }

            QueueItem? observed = null;
            manager.TrackQueued += item => observed = item;

            var requeue = new QueueItem
            {
                SourceType = "youtube",
                SourceId = "rewind_payload",
                Title = "Previous Track",
                Artist = "Previously",
                DurationSeconds = 90,
                RequestedByUserId = "dj",
                RequestedByDisplayName = "DJ",
                GuildId = "g1",
            };

            await manager.RequeueFrontAsync(requeue);

            observed.Should().NotBeNull();
            observed!.Position.Should().Be(0);
            observed.SourceId.Should().Be("rewind_payload");
            observed.QueueItemId.Should().BeGreaterThan(0);

            var queue = manager.GetQueue();
            queue.Should().NotBeEmpty();
            queue[0].SourceId.Should().Be("rewind_payload");
            queue[0].Position.Should().Be(0);
            queue[0].QueueItemId.Should().Be(observed.QueueItemId);
        });
    }

    [Fact]
    public async Task Count_TracksAddsAndRemoves()
    {
        await RunWithQueueManagerAsync(async (manager, _) =>
        {
            manager.Count.Should().Be(0);

            await manager.AddTrackAsync("youtube", "a", "A", "x", 60, "u", "U", "g1");
            await manager.AddTrackAsync("youtube", "b", "B", "x", 60, "u", "U", "g1");
            manager.Count.Should().Be(2);

            await manager.DequeueAsync();
            manager.Count.Should().Be(1);

            await manager.ClearQueueAsync();
            manager.Count.Should().Be(0);
        });
    }

    [Fact]
    public async Task AddTrack_OnUnhydratedManagerWithPersistedRows_LazilyLoadsAndDoesNotCollide()
    {
        // Regression for the multi-tenant lazy-hydration fix: a QueueManager
        // created outside the startup loop (runtime add-server, re-admit after
        // suspend, or a swallowed startup-init failure) is never InitializeAsync'd.
        // Without lazy hydration its in-memory queue starts empty, so the first
        // enqueue computes position 0 and collides with the persisted row on
        // UNIQUE(guild_id, position) — throwing on every /play until restart.
        var config = BuildConfig();
        var stateStore = new StateStore(config, NullLogger<StateStore>.Instance);
        try
        {
            new SchemaMigrator(stateStore.ConnectionString, NullLogger<SchemaMigrator>.Instance).Migrate();
            var queueRepo = new QueueRepository(stateStore);
            var snapshotRepo = new SnapshotRepository(stateStore);

            // Seed 3 persisted rows for g1 via a first, properly-hydrated manager.
            var seeder = new QueueManager(queueRepo, snapshotRepo, config, NullLogger<QueueManager>.Instance, "g1");
            await seeder.InitializeAsync();
            for (int i = 0; i < 3; i++)
            {
                await seeder.AddTrackAsync("youtube", $"seed_{i}", $"Seed {i}", "Seeder", 60, "u", "U", "g1");
            }

            // A brand-new manager for the SAME guild that is never InitializeAsync'd.
            var fresh = new QueueManager(queueRepo, snapshotRepo, config, NullLogger<QueueManager>.Instance, "g1");

            // Must not throw, and must land after the lazily-loaded persisted rows.
            var added = await fresh.AddTrackAsync("youtube", "new", "New", "Newcomer", 60, "u2", "U2", "g1");

            added.Position.Should().Be(3, "the new track follows the 3 lazily-loaded persisted rows");
            var queue = fresh.GetQueue();
            queue.Should().HaveCount(4, "the persisted rows are now visible alongside the new one");
            queue.Select(q => q.Position).Should().Equal(Enumerable.Range(0, 4));
            queue.Select(q => q.SourceId).Should().Equal("seed_0", "seed_1", "seed_2", "new");

            // A second add does not re-hydrate / wipe state — it appends at 4.
            var added2 = await fresh.AddTrackAsync("youtube", "new2", "New 2", "Newcomer", 60, "u2", "U2", "g1");
            added2.Position.Should().Be(4);
            fresh.GetQueue().Should().HaveCount(5);

            // DB agrees: 5 distinct rows, positions 0..4.
            var dbQueue = await queueRepo.GetQueueAsync("g1");
            dbQueue.Should().HaveCount(5);
            dbQueue.Select(q => q.Position).Should().Equal(Enumerable.Range(0, 5));
        }
        finally
        {
            stateStore.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { File.Delete(config.Persistence.SqlitePath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task RequeueFront_InterleavedWithDequeues_StaysConsistent_AndNeverThrows()
    {
        // Regression for the stale-position race: RequeueFrontAsync captured the
        // insert index before its DB await, then moved by that (possibly stale)
        // position after the lock was released — so a concurrent dequeue could make
        // the move target out of bounds (ArgumentOutOfRangeException) or shift it
        // onto the wrong track. The fix backfills + moves by row id under one lock,
        // so these invariants hold under any interleaving.
        await RunWithQueueManagerAsync(async (manager, _) =>
        {
            const int N = 20;
            for (int i = 0; i < N; i++)
            {
                await manager.AddTrackAsync("youtube", $"id_{i}", $"Track {i}", "Artist", 60, "u", "U", "g1");
            }

            var exceptions = new ConcurrentQueue<Exception>();
            var tasks = new List<Task>();

            for (int i = 0; i < 5; i++)
            {
                int local = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await manager.RequeueFrontAsync(new QueueItem
                        {
                            SourceType = "youtube",
                            SourceId = $"rewind_{local}",
                            Title = $"Rewind {local}",
                            Artist = "DJ",
                            DurationSeconds = 90,
                            RequestedByUserId = "dj",
                            RequestedByDisplayName = "DJ",
                            GuildId = "g1",
                        });
                    }
                    catch (Exception ex) { exceptions.Enqueue(ex); }
                }));
            }
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try { await manager.DequeueAsync(); }
                    catch (Exception ex) { exceptions.Enqueue(ex); }
                }));
            }

            await Task.WhenAll(tasks);

            exceptions.Should().BeEmpty("RequeueFront must not throw under concurrent dequeues");

            var queue = manager.GetQueue();
            queue.Select(q => q.Position).Should().Equal(Enumerable.Range(0, queue.Count),
                "positions stay dense and contiguous regardless of interleaving");
            queue.Select(q => q.QueueItemId).Distinct().Should().HaveCount(queue.Count, "no duplicate ids");
        });
    }
}
