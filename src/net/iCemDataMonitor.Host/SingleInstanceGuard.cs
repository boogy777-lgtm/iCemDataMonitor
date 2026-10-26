namespace iCemDataMonitor.Host;

/// <summary>
/// D.3' B.1.2: single-instance guard for the headless host. The host and the
/// shell/tray are DIFFERENT roles — this mutex never collides with
/// Global\iCemDataMonitorTray_v1 (shell) which stays untouched.
/// Fail-fast contract: a second host instance is an operator error — stderr +
/// exit code 1, never a silent no-op.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Production mutex name (B.1.2). Tests pass their own unique name.</summary>
    public const string DefaultMutexName = @"Global\iCemDataMonitorHost_v1";

    private readonly Mutex _mutex;
    private bool _disposed;

    public SingleInstanceGuard(string name = DefaultMutexName)
    {
        Name = name;
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        Acquired = createdNew;
    }

    public string Name { get; }
    public bool Acquired { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Acquired)
        {
            try { _mutex.ReleaseMutex(); }
            catch { /* not owned on this thread — best effort */ }
        }
        _mutex.Dispose();
    }
}
