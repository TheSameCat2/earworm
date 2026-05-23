using System;
using System.Threading;

namespace Earworm;

/// <summary>
/// Application-wide shutdown signal. Singleton in the DI container so any
/// service can inject it and observe <see cref="Token"/> from fire-and-forget
/// <c>Task.Run</c> work or from in-flight HTTP calls. Main wires SIGINT and
/// SIGTERM to <see cref="Cancel"/> so background work can cooperatively unwind
/// before the runtime tears down.
/// </summary>
public sealed class ShutdownLifetime : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public bool IsShuttingDown => _cts.IsCancellationRequested;

    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
    }

    public void Dispose() => _cts.Dispose();
}
