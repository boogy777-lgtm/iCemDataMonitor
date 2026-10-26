using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Halliburton.iCemEmule.Core;
using SharpPcap;

namespace Halliburton.iCemEmule.Emitters
{
    /// <summary>
    /// Contract: ACE LAN multicast emitter (raw 802.3 injection via SharpPcap 6.0).
    /// Builds 152-byte ACE frames per ace_wire constants and injects them with
    /// burst timing (copied from real cRIO pcap) into the selected network adapter.
    /// </summary>
    public interface IAceLanEmitter : IDisposable
    {
        bool IsOpen { get; }
        string? AdapterName { get; }
        long FramesSent { get; }
        /// <summary>List available network adapters (friendly strings). Returns empty if Npcap not installed.</summary>
        static abstract IReadOnlyList<string> ListAdapters();
        /// <summary>Open an adapter by substring match on description or name. Returns "" on success, error message otherwise.</summary>
        string Open(string adapterDescriptionOrName);
        /// <summary>Send one superframe: build 152-byte ACE frames per vector and inject them in a 1 Hz burst (cRIO pcap pattern). Blocking until all frames sent or Close() called.</summary>
        void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac);
        /// <summary>Close the adapter. Idempotent. Cancels in-flight SendSuperframe.</summary>
        void Close();
        /// <summary>Fired on open, close, and error transitions.</summary>
        event Action<string>? StatusChanged;
    }

    /// <summary>
    /// LAN send-device seam (2026-08-29, per the EmuEngineHost emitter-injection
    /// convention): the frame counter / reset-on-open behaviour is provable in
    /// tests WITHOUT Npcap by injecting a fake device source. Production wiring
    /// = the parameterless ctor (real CaptureDeviceList), unchanged.
    /// </summary>
    internal interface ILanSendDevice : IDisposable
    {
        string? Description { get; }
        string? Name { get; }
        /// <summary>6-byte MAC, null when unavailable. Never throws.</summary>
        byte[]? MacBytes { get; }
        void Open();
        void Close();
        void SendPacket(byte[] frame);
    }

    /// <summary>
    /// SharpPcap 6.0-based ACE frame injector.
    /// Thread-safe: lock protects device open/close/send. Volatile _closing flag cancels pacing loop.
    /// </summary>
    public sealed class AceLanEmitter : IAceLanEmitter
    {
        private readonly object _lock = new();
        private readonly Func<IReadOnlyList<ILanSendDevice>> _deviceSource;
        private ILanSendDevice? _device;
        private volatile bool _closing;
        private long _framesSent;
        private volatile bool _sendErrorReported; // guard: report first SendPacket error only per session

        // Паттерн pacing скопирован из реального дампа cRIO (iCemCrio.pcapng.bin):
        // каждый SV шлётся ровно 1 раз в секунду (1 Hz/SV), но не равномерно, а бёрстом
        // в конце секунды. Старт бёрста — на доле секунды ≈ 0.938 (первые ~938 мс тишины),
        // затем N кадров с интервалом ≈ 5.25 мс (~190 Гц), после — пауза ≈ 974 мс.
        // Точное воспроизведение этой формы важно для 1:1-верификации HMI: видимая
        // структура трафика должна совпадать с реальным cRIO, иначе HMI может отличить эмулятор.
        private const double BurstFrameIntervalMs = 5.25; // интервал между кадрами в бёрсте (реальный cRIO: ~190 Гц)
        private const double BurstStartFraction = 0.938; // доля секунды для старта бёрста (≈938 мс тишины перед бёрстом)

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                    return _device != null;
            }
        }

        public string? AdapterName { get; private set; }

        public long FramesSent => Interlocked.Read(ref _framesSent);

        public event Action<string>? StatusChanged;

        /// <summary>Production wiring: real SharpPcap device list.</summary>
        public AceLanEmitter() : this(null)
        {
        }

        /// <summary>Test seam (InternalsVisibleTo iCemEmule.Tests): inject the
        /// device source; null = real CaptureDeviceList (production).</summary>
        internal AceLanEmitter(Func<IReadOnlyList<ILanSendDevice>>? deviceSource)
        {
            _deviceSource = deviceSource ?? RealDevices;
        }

        private static IReadOnlyList<ILanSendDevice> RealDevices() =>
            CaptureDeviceList.Instance
                .Select(d => (ILanSendDevice)new SharpPcapLanDevice(d))
                .ToList();

        /// <summary>True when the Npcap driver looks installed. False = the
        /// empty-adapter-list root cause the UI must name explicitly («Npcap
        /// не установлен»). 2026-08-29: a pure FILE probe — SharpPcap
        /// P/Invokes wpcap.dll (Open's DllNotFoundException), and Npcap
        /// installs it under System32\Npcap (+ System32 in WinPcap-compatible
        /// mode). CaptureDeviceList enumeration costs SECONDS on a live stand
        /// and stalled the engine gate on the first status poll.</summary>
        public static bool IsNpcapAvailable()
        {
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return File.Exists(Path.Combine(sys, "wpcap.dll")) ||
                       File.Exists(Path.Combine(sys, "Npcap", "wpcap.dll"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SharpPcap adapter wrapper behind the test seam. MacBytes
        /// never throws (a MAC read failure used to need try/catch at the
        /// call site).</summary>
        private sealed class SharpPcapLanDevice : ILanSendDevice
        {
            private readonly ILiveDevice _inner;

            public SharpPcapLanDevice(ILiveDevice inner) => _inner = inner;

            public string? Description => _inner.Description;
            public string? Name => _inner.Name;

            public byte[]? MacBytes
            {
                get
                {
                    try { return _inner.MacAddress?.GetAddressBytes(); }
                    catch { return null; }
                }
            }

            public void Open() => _inner.Open(new DeviceConfiguration());
            public void Close() => _inner.Close();
            public void SendPacket(byte[] frame) => _inner.SendPacket(frame);
            public void Dispose() => _inner.Dispose();
        }

        // ── static abstract implementation ──────────────────────────────────

        public static IReadOnlyList<string> ListAdapters()
        {
            try
            {
                var devices = CaptureDeviceList.Instance;
                return devices
                    .Select(d => $"{d.Description} ({d.Name})")
                    .ToList();
            }
            catch
            {
                // Npcap not installed or inaccessible — return empty
                return Array.Empty<string>();
            }
        }

        // ── instance methods ────────────────────────────────────────────────

        public string Open(string adapterDescriptionOrName)
        {
            if (string.IsNullOrWhiteSpace(adapterDescriptionOrName))
                return "Adapter name must not be empty.";

            lock (_lock)
            {
                CloseInternal();

                try
                {
                    var devices = _deviceSource();
                    ILanSendDevice? found = null;

                    // Substring match on Description, Name, or combined label (as produced by ListAdapters)
                    foreach (var d in devices)
                    {
                        string desc = d.Description ?? "";
                        string name = d.Name ?? "";
                        string combined = $"{desc} ({name})";
                        if (desc.IndexOf(adapterDescriptionOrName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf(adapterDescriptionOrName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            combined.IndexOf(adapterDescriptionOrName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = d;
                            break;
                        }
                    }

                    // Fallback: exact Name match
                    if (found == null)
                        found = devices.FirstOrDefault(d =>
                            string.Equals(d.Name, adapterDescriptionOrName, StringComparison.OrdinalIgnoreCase));

                    if (found == null)
                    {
                        var msg = $"Adapter not found: '{adapterDescriptionOrName}'. Available: {string.Join(", ", devices.Select(d => d.Name))}";
                        StatusChanged?.Invoke(msg);
                        return msg;
                    }

                    // Open for injection (default DeviceConfiguration = no capture, no promiscuous)
                    found.Open();
                    _device = found;
                    AdapterName = adapterDescriptionOrName;
                    _closing = false;
                    _sendErrorReported = false;
                    // 2026-08-29 (frame-sent feedback): a fresh open / adapter
                    // change starts a NEW emission session — the counter the
                    // operator reads («кадров отправлено: N») restarts at 0.
                    Interlocked.Exchange(ref _framesSent, 0);

                    // Build detailed device info for StatusChanged
                    var mac = found.MacBytes;
                    string macStr = mac is null ? "N/A" : string.Join(":", mac.Select(b => b.ToString("X2")));
                    var success = $"LAN adapter opened: {found.Description ?? found.Name} | Device={found.Name} | MAC={macStr}";
                    StatusChanged?.Invoke(success);
                    return "";
                }
                catch (DllNotFoundException)
                {
                    var msg = "Npcap not found. Install Npcap from https://npcap.com/ (select 'WinPcap API-compatible mode').";
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    // Detect permission errors and append hint
                    if (msg.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("denied", StringComparison.OrdinalIgnoreCase))
                    {
                        msg += " (try running as Administrator)";
                    }
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
            }
        }

        public void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac)
        {
            ILanSendDevice? device;
            lock (_lock)
            {
                if (_device == null || _closing)
                    return;
                device = _device;
            }

            if (vectors == null || vectors.Count == 0)
                return;

            byte[] srcMacBytes;
            try
            {
                // If user left srcMac empty, auto-use the opened adapter's real MAC
                if (string.IsNullOrWhiteSpace(srcMac))
                {
                    lock (_lock)
                    {
                        var mac = _device?.MacBytes;
                        if (mac != null)
                            srcMacBytes = mac;
                        else
                        {
                            StatusChanged?.Invoke("Src MAC not set and adapter has no MAC. Use the textbox.");
                            return;
                        }
                    }
                }
                else
                {
                    srcMacBytes = AceFrameBuilder.ParseMac(srcMac);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Invalid src MAC '{srcMac}': {ex.Message}");
                return;
            }

            // Бёрст-паттерн реального cRIO (iCemCrio.pcapng.bin): 1 Hz/SV, но форма не равномерная —
            // в конце секунды бёрст из N кадров с интервалом ≈ 5.25 мс (~190 Гц), старт ≈0.938 с,
            // затем пауза ≈ 974 мс. Это критично для 1:1-верификации HMI.
            //
            // Интервал между соседними кадрами бёрста: (int)Math.Max(1, Math.Round(5.25)) = 5 мс.
            int burstIntervalMs = (int)Math.Max(1, Math.Round(BurstFrameIntervalMs));

            // Старт бёрста: доля секунды ≈ 0.938 (первый кадр — после ~938 мс тишины).
            // Если кадров больше 6 (например 20 при включённых firmware-таблицах), бёрст длиннее:
            // N×5.25 мс. Чтобы конец бёрста не вылезал за границу секунды (1.0 с), сдвигаем старт
            // раньше: 1.0 − N×5.25/1000 − 0.01. Для 20 кадров: 1.0 − 0.105 − 0.01 = 0.885.
            double startFraction = BurstStartFraction;
            if (vectors.Count > 6)
                startFraction = Math.Min(BurstStartFraction, 1.0 - vectors.Count * BurstFrameIntervalMs / 1000.0 - 0.01);

            int initialSleepMs = (int)(startFraction * 1000);

            for (int i = 0; i < vectors.Count; i++)
            {
                // Honour cancellation — volatile read
                if (_closing)
                    break;

                // Пауза перед первым кадром = старт бёрста; между соседними кадрами — интервал бёрста
                Thread.Sleep(i == 0 ? initialSleepMs : burstIntervalMs);

                // Re-check cancellation after sleep
                if (_closing)
                    break;

                var vector = vectors[i];
                byte[] frame;
                try
                {
                    frame = AceFrameBuilder.BuildFrame(srcMacBytes, equipmentNumber, vector.SvNumber, vector.Payload);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"BuildFrame error (sv={vector.SvNumber:X8}): {ex.Message}");
                    continue;
                }

                lock (_lock)
                {
                    if (_device == null || _closing)
                        break;

                    try
                    {
                        _device.SendPacket(frame);
                        Interlocked.Increment(ref _framesSent);
                    }
                    catch (Exception ex)
                    {
                        // Report first send error per session only (avoid UI spam at 1 Hz tick)
                        if (!_sendErrorReported)
                        {
                            _sendErrorReported = true;
                            var errMsg = $"SendPacket error (sv=0x{vector.SvNumber:X8}): {ex.GetType().Name}: {ex.Message}";
                            if (ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                                errMsg += " (try running as Administrator)";
                            StatusChanged?.Invoke(errMsg);
                        }
                    }
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                CloseInternal();
            }
        }

        private void CloseInternal()
        {
            _closing = true;
            if (_device != null)
            {
                try { _device.Close(); } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
                AdapterName = null;
                StatusChanged?.Invoke("LAN adapter closed");
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
