using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Production IAceFrameSource that captures ACE frames via SharpPcap/Npcap.
/// BPF filter: "ether multicast", promiscuous, 64 KB kernel buffer.
/// Fires FrameReceived for each raw Ethernet frame received.
/// </summary>
internal sealed class SharpPcapAceSource : IAceFrameSource
{
    private readonly string _adapterName;
    private LibPcapLiveDevice? _device;

    public event Action<byte[]>? FrameReceived;

    public SharpPcapAceSource(string adapterName)
    {
        _adapterName = adapterName;
    }

    public string AdapterName => _adapterName;
    public string MacAddress => _device?.MacAddress?.ToString() ?? "";

    public bool IsRunning => _device?.Started ?? false;

    public void Start()
    {
        var query = _adapterName;
        _device = LibPcapLiveDeviceList.Instance
            .FirstOrDefault(d =>
                (d.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (d.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                ($"{d.Description} ({d.Name})".Contains(query, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"Adapter '{_adapterName}' not found");

        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness,
            ReadTimeout = 1000,
            BufferSize = 64 * 1024,
        });
        _device.Filter = "ether multicast";

        _device.OnPacketArrival += OnPacket;
        _device.StartCapture();
    }

    public void Stop()
    {
        if (_device == null) return;

        try
        {
            _device.StopCapture();
            _device.OnPacketArrival -= OnPacket;
            _device.Close();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
        _device = null;
    }

    private void OnPacket(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            if (raw?.Data != null)
            {
                // Extract just the Ethernet frame bytes (strip pcap header)
                // SharpPcap PacketCapture provides the full raw packet including link layer
                var linkPacket = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                if (linkPacket is EthernetPacket eth)
                {
                    FrameReceived?.Invoke(eth.Bytes);
                }
            }
        }
        catch
        {
            // Silently drop malformed packets — they won't survive ACE validation anyway
        }
    }
}