namespace iCemDataMonitor.Transport;

/// <summary>
/// Seam for unit-testing ACE frame capture without real Npcap hardware.
/// Production: SharpPcapAceSource (live adapter).
/// Test: ReplayAceFrameSource (pcapng replay).
/// </summary>
public interface IAceFrameSource : IDisposable
{
    /// <summary>Fired on each received raw Ethernet frame (152 bytes for ACE).</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Start capturing frames.</summary>
    void Start();

    /// <summary>Stop capturing frames (¬ restartable; dispose to reopen).</summary>
    void Stop();

    /// <summary>Human-readable adapter name for status.</summary>
    string AdapterName { get; }

    /// <summary>MAC address of the capture interface.</summary>
    string MacAddress { get; }

    /// <summary>Whether the source is actively receiving frames.</summary>
    bool IsRunning { get; }
}