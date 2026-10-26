namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// Test IAceFrameSource that replays frames from a pcapng file.
/// Use the PcapCapture constructor for unit tests that don't need a real file.
/// </summary>
public sealed class ReplayAceFrameSource : IAceFrameSource
{
    private readonly PcapCapture _capture;
    private Thread? _replayThread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event Action<byte[]>? FrameReceived;

    /// <summary>Create from pcapng file path.</summary>
    public ReplayAceFrameSource(string pcapngPath)
    {
        _capture = PcapngReader.Read(pcapngPath);
    }

    /// <summary>Create from pre-loaded capture (for unit tests that filter before replay or use empty capture).</summary>
    public ReplayAceFrameSource(PcapCapture capture)
    {
        _capture = capture;
    }

    public string AdapterName => $"replay:{_capture.Packets.Count}frames";
    public string MacAddress => "00:80:2f:23:f4:44";
    public bool IsRunning => _replayThread?.IsAlive ?? false;

    public int TotalFrames => _capture.Packets.Count;
    public int AceFrameCount => _capture.Packets.Count(p =>
        p.Frame.Length == 152 && p.Frame[14] == 0x03 && p.Frame[15] == 0x03
        && (p.Frame[16] == 0x08 || p.Frame[16] == 0x09));

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _replayThread = new Thread(() => ReplayLoop(_cts.Token))
        {
            Name = "ReplayAceFrameSource",
            IsBackground = true,
        };
        _replayThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _replayThread?.Join(TimeSpan.FromSeconds(10));
        _replayThread = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void ReplayLoop(CancellationToken ct)
    {
        // Replay all frames as fast as possible (no timing simulation).
        // For timed replay, the test can control pacing externally.
        foreach (var pkt in _capture.Packets)
        {
            if (ct.IsCancellationRequested) break;
            FrameReceived?.Invoke(pkt.Frame);
        }
    }
}