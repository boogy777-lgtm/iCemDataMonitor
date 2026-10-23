namespace iCemDataMonitor.Host.Tray;

/// <summary>Owns both host command waiters and their shared cancellation handle.</summary>
internal sealed class HostCommandListeners : IDisposable
{
    private readonly EventWaitHandle _restart;
    private readonly EventWaitHandle _shutdown;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Task _restartWorker;
    private readonly Task _shutdownWorker;
    private readonly CancellationTokenRegistration _registration;
    private readonly object _disposeLock = new();
    private bool _disposed;

    internal HostCommandListeners(string restartName, string shutdownName,
        CancellationToken stopping, Action restart, Action close)
    {
        EventWaitHandle? restartEvent = null;
        EventWaitHandle? shutdownEvent = null;
        Task? restartWorker = null;
        Task? shutdownWorker = null;
        CancellationTokenRegistration registration = default;
        try
        {
            restartEvent = new EventWaitHandle(false, EventResetMode.AutoReset, restartName);
            restartEvent.Reset();
            shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shutdownName);
            shutdownEvent.Reset();
            registration = stopping.Register(() => _stop.Set());
            restartWorker = StartWorker(restartEvent, restart);
            shutdownWorker = StartWorker(shutdownEvent, close);
            _restart = restartEvent;
            _shutdown = shutdownEvent;
            _restartWorker = restartWorker;
            _shutdownWorker = shutdownWorker;
            _registration = registration;
        }
        catch
        {
            _stop.Set();
            try { Task.WhenAll(new[] { restartWorker, shutdownWorker }.OfType<Task>()).GetAwaiter().GetResult(); }
            finally
            {
                registration.Dispose();
                restartEvent?.Dispose();
                shutdownEvent?.Dispose();
                _stop.Dispose();
            }
            throw;
        }
    }

    private Task StartWorker(EventWaitHandle command, Action dispatch) => Task.Factory.StartNew(() =>
    {
        // Stop has priority if shutdown and a command are signalled together.
        var handles = new WaitHandle[] { _stop, command };
        while (WaitHandle.WaitAny(handles) == 1) dispatch();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal Task Completion => Task.WhenAll(_restartWorker, _shutdownWorker);

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            try { Completion.GetAwaiter().GetResult(); }
            finally
            {
                _registration.Dispose();
                _restart.Dispose();
                _shutdown.Dispose();
                _stop.Dispose();
            }
        }
    }
}
