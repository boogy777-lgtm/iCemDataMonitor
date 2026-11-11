namespace iCemDataMonitor.HostClient;

/// <summary>Serializes theme commands and reads within one UI session. Entry
/// points and apply run on the caller's UI context; no WinUI dependency.</summary>
public sealed class UiThemeCoordinator : IDisposable
{
    private readonly Func<bool> _read;
    private readonly Func<bool, bool> _write;
    private readonly Action<bool> _apply;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private IHostApi? _api;
    private Func<bool> _connected = () => false;
    private CancellationTokenSource _session = new();
    private bool _closed;

    public UiThemeCoordinator(Func<bool> read, Func<bool, bool> write, Action<bool> apply, Action<string> log)
    {
        _read = read; _write = write; _apply = apply; _log = log;
    }

    public Task AttachAsync(IHostApi? api, Func<bool> connected)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            old = _session;
            _session = new();
            _tail = Task.CompletedTask;
            _api = api;
            _connected = connected;
        }
        old.Cancel();
        return RefreshAsync();
    }

    public Task ToggleAsync(bool light) => Enqueue(light, localRead: false);
    public Task RefreshAsync() => Enqueue(null, localRead: false);
    public Task ReadLocalAsync() => Enqueue(null, localRead: true);

    private Task Enqueue(bool? desired, bool localRead)
    {
        Task previous;
        CancellationTokenSource session;
        IHostApi? api;
        Func<bool> connected;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            previous = _tail;
            session = _session;
            api = _api;
            connected = _connected;
            // Reserve the slot BEFORE executing any external callback. Reentry
            // then queues behind this operation, even for synchronous RPCs.
            _tail = completed.Task;
        }
        return CompleteAsync();

        async Task CompleteAsync()
        {
            try { await RunAsync(previous, session, api, connected, desired, localRead).ConfigureAwait(true); }
            finally { completed.SetResult(); } // a failing consumer cannot poison the queue
        }
    }

    private async Task RunAsync(Task previous, CancellationTokenSource session, IHostApi? api,
        Func<bool> connected, bool? desired, bool localRead)
    {
        await previous.ConfigureAwait(true);
        if (session.IsCancellationRequested) return;
        bool current;
        try
        {
            // Routing is chosen BEFORE any RPC is sent. An exception after
            // send has unknown outcome: never invoke a second writer.
            if (localRead) current = _read();
            else if (api is null || !connected()) current = desired.HasValue ? _write(desired.Value) : _read();
            else
            {
                var reply = desired.HasValue
                    ? await api.SetUiThemeAsync(desired.Value, session.Token).ConfigureAwait(true)
                    : await api.GetUiPrefsAsync(session.Token).ConfigureAwait(true);
                current = reply.ThemeLight;
                if (!session.IsCancellationRequested)
                    _log($"[theme] current host theme: {(current ? "light" : "dark")}");
            }
        }
        catch (Exception ex)
        {
            if (session.IsCancellationRequested) return;
            _log($"[theme] RPC/read unconfirmed ({ex.Message}); reconciling station projection");
            current = _read();
        }
        bool active;
        lock (_gate) active = !_closed && ReferenceEquals(session, _session) && !session.IsCancellationRequested;
        // All entry points, including Dispose/Attach, belong to the same UI
        // context. Do not put UI callbacks under the bookkeeping lock.
        if (active) _apply(current);
    }

    public void Dispose()
    {
        CancellationTokenSource session;
        lock (_gate) { _closed = true; session = _session; }
        session.Cancel();
    }
}
