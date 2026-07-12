using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Earworm.Config;

namespace Earworm.Persistence;

public sealed class StateStore : IDisposable
{
    private readonly EarwormConfig _config;
    private readonly ILogger<StateStore> _logger;
    private readonly string _connectionString;
    private readonly Channel<IWriteJob> _writeChannel;
    private readonly Task _writeWorkerTask;
    private readonly CancellationTokenSource _cts = new();
    private IWriteJob? _currentJob;
    private int _pendingWriteCount;
    private volatile bool _writerHealthy = true;
    private int _disposed;

    public string ConnectionString => _connectionString;

    /// <summary>True while the single-writer worker can service write jobs.</summary>
    public bool IsWriterHealthy => _writerHealthy && !_writeWorkerTask.IsCompleted;

    /// <summary>Writes waiting for capacity, queued, or currently executing.</summary>
    public int PendingWriteCount => Interlocked.CompareExchange(ref _pendingWriteCount, 0, 0);

    public StateStore(EarwormConfig config, ILogger<StateStore> logger)
    {
        _config = config;
        _logger = logger;

        // Ensure database directory exists
        var sqlitePath = _config.Persistence.SqlitePath;
        var parentDir = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        // Build SQLite connection string
        _connectionString = $"Data Source={sqlitePath};";

        ApplyDbScopedPragmas();

        // Bound the shared writer queue so a burst from one tenant applies
        // backpressure instead of consuming memory without limit.
        int capacity = Math.Max(1, _config.Ops.WriteQueueCapacity);
        _writeChannel = Channel.CreateBounded<IWriteJob>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Spin up background single-writer task
        _writeWorkerTask = Task.Run(ProcessWritesAsync);
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.StateChange += (sender, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                ApplyPragmas(connection);
            }
        };
        return connection;
    }

    private void ApplyDbScopedPragmas()
    {
        try
        {
            using var bootstrap = new SqliteConnection(_connectionString);
            bootstrap.Open();
            using var cmd = bootstrap.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA cache_size = -64000;
                PRAGMA auto_vacuum = INCREMENTAL;
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("StateStore DB-scoped pragmas applied.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply DB-scoped pragmas at startup.");
            throw;
        }
    }

    private void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task<T> SubmitWriteAsync<T>(Func<SqliteConnection, Task<T>> writeFunc)
    {
        var job = new WriteJob<T>(writeFunc);
        Interlocked.Increment(ref _pendingWriteCount);
        try
        {
            await _writeChannel.Writer.WriteAsync(job);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWriteCount);
            throw;
        }
        return await job.Task;
    }

    public async Task SubmitWriteAsync(Func<SqliteConnection, Task> writeAction)
    {
        var job = new VoidWriteJob(writeAction);
        Interlocked.Increment(ref _pendingWriteCount);
        try
        {
            await _writeChannel.Writer.WriteAsync(job);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWriteCount);
            throw;
        }
        await job.Task;
    }

    private async Task ProcessWritesAsync()
    {
        _logger.LogInformation("StateStore single-writer worker started.");

        // Keep one persistent connection for write channel execution
        using var connection = CreateConnection();
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to open persistent writer connection to SQLite.");
            FailChannelAndDrain(ex);
            return;
        }

        // Drive PRAGMA incremental_vacuum periodically. auto_vacuum = INCREMENTAL
        // by itself only marks pages as reclaimable; without the explicit vacuum
        // the freelist accumulates and the file size never shrinks (tech plan §4.1).
        int writesSinceVacuum = 0;
        const int writesPerVacuum = 200;

        try
        {
            await foreach (var job in _writeChannel.Reader.ReadAllAsync(_cts.Token))
            {
                _currentJob = job;
                try
                {
                    await job.ExecuteAsync(connection);
                }
                catch (Exception jobEx)
                {
                    _logger.LogError(jobEx, "Write job execution threw outside its TCS contract.");
                    job.Fault(jobEx);
                }
                finally
                {
                    _currentJob = null;
                    Interlocked.Decrement(ref _pendingWriteCount);
                }

                if (++writesSinceVacuum >= writesPerVacuum)
                {
                    writesSinceVacuum = 0;
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "PRAGMA incremental_vacuum;";
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Incremental vacuum failed; will retry on next cadence.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("StateStore single-writer worker canceled.");
            _writeChannel.Writer.TryComplete();
            FailChannelAndDrain(new OperationCanceledException("StateStore writer canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in StateStore single-writer worker loop.");
            FailChannelAndDrain(ex);
        }
        finally
        {
            _writerHealthy = false;
            _logger.LogInformation("StateStore single-writer worker shutting down.");
        }
    }

    private void FailChannelAndDrain(Exception ex)
    {
        _writeChannel.Writer.TryComplete(ex);

        var inFlight = System.Threading.Interlocked.Exchange(ref _currentJob, null);
        if (inFlight != null)
        {
            inFlight.Fault(ex);
            Interlocked.Decrement(ref _pendingWriteCount);
        }

        while (_writeChannel.Reader.TryRead(out var pending))
        {
            pending.Fault(ex);
            Interlocked.Decrement(ref _pendingWriteCount);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop accepting new jobs, then let ReadAllAsync drain every accepted
        // write. Cancellation is only the bounded-time fallback; cancelling at
        // the same time as TryComplete discards the very queue we want to save.
        _writeChannel.Writer.TryComplete();

        bool workerStopped = false;

        try
        {
            // Give the worker up to 5s to drain accepted writes and exit.
            // Docker sends SIGTERM with a 10s grace, so 5s leaves headroom
            // for the outer shutdown sequence (Lavalink disconnect, etc.).
            workerStopped = _writeWorkerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StateStore writer faulted while draining during shutdown.");
            workerStopped = true;
        }

        if (!workerStopped)
        {
            _logger.LogWarning(
                "StateStore writer did not drain within 5s; cancelling with {PendingWrites} write(s) pending.",
                PendingWriteCount);
            _cts.Cancel();
            try
            {
                workerStopped = _writeWorkerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StateStore writer faulted after shutdown cancellation.");
                workerStopped = true;
            }
        }

        // WAL checkpoint: flush the write-ahead log into the main database
        // file so a subsequent process doesn't need to replay the WAL. Use
        // TRUNCATE mode to shrink the WAL file to zero.
        try
        {
            if (!workerStopped)
            {
                _logger.LogWarning("Skipping WAL checkpoint because the writer is still executing.");
                return;
            }

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // Set per-connection pragmas so busy_timeout applies and we don't
            // fail immediately if another process (e.g. Docker healthcheck
            // reading the DB) holds a shared lock.
            ApplyPragmas(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("StateStore WAL checkpointed (TRUNCATE).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint on dispose failed; WAL will be replayed on next open.");
        }
        finally
        {
            if (workerStopped)
            {
                _cts.Dispose();
            }
        }
    }

    private interface IWriteJob
    {
        Task ExecuteAsync(SqliteConnection connection);
        void Fault(Exception ex);
    }

    private sealed class WriteJob<T> : IWriteJob
    {
        private readonly Func<SqliteConnection, Task<T>> _func;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteJob(Func<SqliteConnection, Task<T>> func) => _func = func;

        public Task<T> Task => _tcs.Task;

        public async Task ExecuteAsync(SqliteConnection connection)
        {
            try
            {
                var result = await _func(connection);
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Fault(Exception ex) => _tcs.TrySetException(ex);
    }

    private sealed class VoidWriteJob : IWriteJob
    {
        private readonly Func<SqliteConnection, Task> _action;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public VoidWriteJob(Func<SqliteConnection, Task> action) => _action = action;

        public Task Task => _tcs.Task;

        public async Task ExecuteAsync(SqliteConnection connection)
        {
            try
            {
                await _action(connection);
                _tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Fault(Exception ex) => _tcs.TrySetException(ex);
    }
}
