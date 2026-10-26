using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport.Tests;

public class AceSnifferServiceTests
{
    // ═══ Path helpers ═══

    private static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir is not null; i++)
            {
                // 2026-09-01 repo restructure: the Wireshark captures moved to
                // docs\Wireshark (plain Wireshark\ kept for older checkouts).
                if (Directory.Exists(Path.Combine(dir, "docs", "Wireshark")) ||
                    Directory.Exists(Path.Combine(dir, "Wireshark")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException(
                "Cannot find repo root (docs/Wireshark captures directory)");
        }
    }

    /// <summary>Resolve a capture file under the repo's Wireshark directory
    /// (docs\Wireshark since the 2026-09-01 restructure, plain Wireshark\
    /// before it).</summary>
    private static string CapturePath(string fileName)
    {
        var root = RepoRoot;
        var p = Path.Combine(root, "docs", "Wireshark", fileName);
        if (File.Exists(p)) return p;
        return Path.Combine(root, "Wireshark", fileName);
    }

    private static string MainPcapngPath => CapturePath("iCemCrio.pcapng.bin");

    private static string CrioTest4PcapngPath => CapturePath("CrioTest4.pcapng");

    private static string OfficePcapngPath => CapturePath("iCem без крио.pcapng.bin");

    private static LantableRegistry Lantable
    {
        get
        {
            // Load from Build output (copied by iCemDataMonitor.Plugins.Icem build;
            // net10.0 since the wave-2 TFM migration — net8.0 output no longer exists)
            var path = Path.Combine(RepoRoot, "iCemDataMonitor.Plugins.Icem",
                "bin", "Debug", "net10.0", "templates", "lantable_registry.json");
            if (!File.Exists(path))
            {
                // Try alternate locations
                path = Path.Combine(AppContext.BaseDirectory, "templates", "lantable_registry.json");
            }
            if (!File.Exists(path))
            {
                // Fallback: walk to find it
                path = Path.Combine(RepoRoot, "iCemDataMonitor.Plugins.Icem",
                    "templates", "lantable_registry.json");
            }
            return LantableRegistry.Load(path);
        }
    }

    private static AceSnifferOptions CreateOptions() => new()
    {
        Adapter = "test-replay",
        CaptureDir = Path.Combine(Path.GetTempPath(), "ace_test_captures"),
        Enabled = true,
    };

    // ═══ REPLAY-FEEDER acceptance test (core criterion) ═══

    [Fact]
    public async Task ReplayFeeder_6978Frames_6Sv_1Equipment_CorrectPressureRange()
    {
        // Arrange
        var lantable = Lantable;
        var capture = PcapngReader.Read(MainPcapngPath);
        var source = new ReplayAceFrameSource(capture);
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;

        var service = new AceSnifferService(
            Options.Create(options), logger, lantable, _ => source);

        var decodedFrames = new List<(DateTime ts, uint eq, uint sv, IReadOnlyList<DecodedLanValue> signals)>();
        service.AceFrameDecoded += (ts, eq, sv, signals) =>
            decodedFrames.Add((ts, eq, sv, signals));

        // Act — start the service and let it process all replayed frames.
        // Poll until the processed-frame counter settles (replay fully drained
        // through the frame channel) — a fixed sleep flakes under parallel load.
        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);
        await WaitForReplaySettledAsync(service);
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Diagnostic: check LantableRegistry state
        Assert.Equal(194, lantable.Count);
        Assert.True(lantable.TryGet(0x13120200u, out var _));
        Assert.True(lantable.TryGet(0x13120206u, out var _));
        Assert.True(lantable.TryGet(0x13120203u, out var _));
        Assert.True(lantable.TryGet(0x13120209u, out var _));
        Assert.True(lantable.TryGet(0x1312020Cu, out var _));
        Assert.True(lantable.TryGet(0x1312020Fu, out var _));

        // Assert — frame count
        var aceFrames = decodedFrames;
        Assert.Equal(6978, aceFrames.Count);

        // Assert — 6 distinct service vectors
        var svSet = aceFrames.Select(f => f.sv).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(6, svSet.Count);
        uint[] expectedSvs = [0x13120200, 0x13120203, 0x13120206, 0x13120209, 0x1312020C, 0x1312020F];
        foreach (var esv in expectedSvs)
            Assert.Contains(esv, svSet);

        // Assert — 1 equipment (0x00A86F46)
        var eqSet = aceFrames.Select(f => f.eq).Distinct().ToList();
        Assert.Single(eqSet);
        Assert.Equal(0x00A86F46u, eqSet[0]);

        // Assert — Pump A Pressure in [120, 130] psi (sentinels excluded)
        var pumpAFrames = aceFrames.Where(f => f.sv == 0x13120206u).ToList();
        Assert.NotEmpty(pumpAFrames);
        var pressures = new List<double>();
        foreach (var f in pumpAFrames.Take(200)) // sample first 200
        {
            foreach (var sig in f.signals)
            {
                if (sig.SignalName == "Pump A Pressure" && sig.Value.HasValue && !sig.IsSentinel)
                    pressures.Add(sig.Value.Value);
            }
        }
        Assert.NotEmpty(pressures);
        Assert.All(pressures, p => Assert.InRange(p, 120.0, 130.0));

        // Assert — unknownVectors = 0, decodeErrors = 0
        // Wait a bit more for any in-flight frames to finish
        await Task.Delay(500);
        var status = service.GetStatus();
        // totalFrames counts ALL frames (ACE + non-ACE); the pcapng has 7225 total, 6978 ACE
        Assert.True(status.TotalFrames >= 6978,
            $"totalFrames={status.TotalFrames}, unknownVectors={status.UnknownVectors}, decodeErrors={status.DecodeErrors}, aceFrames.Count={aceFrames.Count}");
        Assert.Equal(0, status.DecodeErrors);
        // Check that svs in decoded frames all resolve
        var decodedSvs = aceFrames.Select(f => f.sv).Distinct().ToHashSet();
        foreach (var sv in decodedSvs)
            Assert.True(lantable.TryGet(sv, out var _), $"SV 0x{sv:X8} not found in lantable");
        Assert.Equal(0, status.UnknownVectors);

        // Assert — prefix counts: only 030308
        Assert.True(status.PrefixCounts["030308"] > 0);
        Assert.Equal(0, status.PrefixCounts["030309"]);
    }

    // ═══ Negative tests: wrong-prefix captures yield zero ACE frames ═══

    [Fact]
    public async Task ReplayFeeder_CrioTest4_YieldsZeroAceFrames()
    {
        var lantable = Lantable;
        var capture = PcapngReader.Read(CrioTest4PcapngPath);
        var source = new ReplayAceFrameSource(capture);
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;

        var service = new AceSnifferService(
            Options.Create(options), logger, lantable, _ => source);

        var decodedFrames = new List<(DateTime ts, uint eq, uint sv, IReadOnlyList<DecodedLanValue> signals)>();
        service.AceFrameDecoded += (ts, eq, sv, signals) =>
            decodedFrames.Add((ts, eq, sv, signals));

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);
        await WaitForReplaySettledAsync(service);
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(5000));

        // CrioTest4 has 87 frames, all linktype 0 (NULL), none are valid ACE
        Assert.Empty(decodedFrames);
        Assert.True(source.TotalFrames > 0, "Source should have frames");
        Assert.Equal(0, source.AceFrameCount);
    }

    [Fact]
    public async Task ReplayFeeder_OfficeCapture_YieldsZeroAceFrames()
    {
        var lantable = Lantable;
        var capture = PcapngReader.Read(OfficePcapngPath);
        var source = new ReplayAceFrameSource(capture);
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;

        var service = new AceSnifferService(
            Options.Create(options), logger, lantable, _ => source);

        var decodedFrames = new List<(DateTime ts, uint eq, uint sv, IReadOnlyList<DecodedLanValue> signals)>();
        service.AceFrameDecoded += (ts, eq, sv, signals) =>
            decodedFrames.Add((ts, eq, sv, signals));

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);
        await WaitForReplaySettledAsync(service);
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(5000));

        Assert.Empty(decodedFrames);
        Assert.True(source.TotalFrames > 0, "Source should have frames");
        Assert.Equal(0, source.AceFrameCount);
    }

    // ═══ NO_ADAPTER: no adapter configured ⇒ ADAPTER_MISSING or Ethernet auto-select ═══
    //
    // Current doctrine (AceSnifferService FSM): with Adapter==null the service
    // attempts AUTO-SELECT of the first Ethernet adapter. Therefore the outcome
    // depends on the machine:
    //   - Ethernet NIC present  ⇒ auto-select succeeds ⇒ RUNNING (injected replay source)
    //   - no NIC (or none matching Ethernet keywords) ⇒ ADAPTER_MISSING
    // Both are valid states; the service must be ALIVE and AvailableAdapters populated.

    [Fact]
    public async Task NoAdapterConfigured_EntersMissingOrAutoSelects()
    {
        var lantable = Lantable;
        var capture = new PcapCapture(); // empty capture
        capture.LinkTypes.Add(1);
        var source = new ReplayAceFrameSource(capture);

        // No Adapter set — simulates operator not having chosen one yet
        var options = new AceSnifferOptions
        {
            Adapter = null,
            CaptureDir = Path.Combine(Path.GetTempPath(), "ace_test_captures"),
            Enabled = true,
            RetryIntervalSeconds = 1, // fast retry for test
        };
        var logger = NullLogger<AceSnifferService>.Instance;

        var service = new AceSnifferService(
            Options.Create(options), logger, lantable, _ => source);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        // Wait for the FSM to settle in a terminal branch (poll, then read
        // status BEFORE cancel — ExecuteAsync sets STOPPED on shutdown).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var state = service.GetStatus().State;
        while (state != "ADAPTER_MISSING" && state != "RUNNING" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            state = service.GetStatus().State;
        }

        var status = service.GetStatus();
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Valid terminal states under current doctrine — never a crash.
        Assert.True(status.State == "ADAPTER_MISSING" || status.State == "RUNNING",
            $"Unexpected state '{status.State}' — expected ADAPTER_MISSING or RUNNING (auto-select)");
        if (status.State == "ADAPTER_MISSING")
        {
            Assert.False(status.Up);
        }
        // availableAdapters should be populated (even if empty on test machine w/o Npcap)
        Assert.NotNull(status.AvailableAdapters);
        // service is alive — not crashed
        Assert.NotNull(status);
    }

    // ═══ DTO shape tests vs mock fixtures ═══

    [Fact]
    public void GetStatus_MatchesSnifferStatusMockShape()
    {
        var status = new AceSnifferStatus();

        // Verify property names matching the mock fixture "ace" object
        var type = typeof(AceSnifferStatus);
        Assert.NotNull(type.GetProperty("Adapter"));
        Assert.NotNull(type.GetProperty("MacAddress"));
        Assert.NotNull(type.GetProperty("Up"));
        Assert.NotNull(type.GetProperty("FramesPerSec"));
        Assert.NotNull(type.GetProperty("LastFrameAgeSec"));
        Assert.NotNull(type.GetProperty("TotalFrames"));
        Assert.NotNull(type.GetProperty("DecodeErrors"));
        Assert.NotNull(type.GetProperty("PrefixCounts"));
        Assert.NotNull(type.GetProperty("Equipment"));
    }

    [Fact]
    public void GetServiceVectors_MatchesServiceVectorsMockShape()
    {
        var info = new AceServiceVectorInfo();

        var type = typeof(AceServiceVectorInfo);
        Assert.NotNull(type.GetProperty("VectorId"));
        Assert.NotNull(type.GetProperty("Name"));
        Assert.NotNull(type.GetProperty("IsUnknown"));
        Assert.NotNull(type.GetProperty("FrameRate"));
        Assert.NotNull(type.GetProperty("LastSeenSec"));
        Assert.NotNull(type.GetProperty("DecodedSignalCount"));
    }

    [Fact]
    public void GetSignalPreview_MatchesSignalPreviewMockShape()
    {
        var preview = new AceSignalPreview();

        var type = typeof(AceSignalPreview);
        Assert.NotNull(type.GetProperty("Name"));
        Assert.NotNull(type.GetProperty("Value"));
        Assert.NotNull(type.GetProperty("Uom"));
        Assert.NotNull(type.GetProperty("Sentinel"));
    }

    [Fact]
    public void GetCaptureStatus_MatchesExpectedShape()
    {
        var status = new AceCaptureStatus();

        var type = typeof(AceCaptureStatus);
        Assert.NotNull(type.GetProperty("Capturing"));
        Assert.NotNull(type.GetProperty("FramesCaptured"));
        Assert.NotNull(type.GetProperty("ElapsedSec"));
        Assert.NotNull(type.GetProperty("TargetPath"));
        Assert.NotNull(type.GetProperty("StartedAt"));
    }

    // ═══ Capture-to-file test ═══

    [Fact]
    public async Task CaptureToFile_WritesValidPcapng_WithCorrectFrameCount()
    {
        var lantable = Lantable;
        var capture = PcapngReader.Read(MainPcapngPath);
        // Only use first 60 ACE frames for fast capture test
        var acePackets = capture.Packets
            .Where(p => p.Frame.Length == 152
                     && p.Frame[14] == 0x03 && p.Frame[15] == 0x03
                     && (p.Frame[16] == 0x08 || p.Frame[16] == 0x09))
            .Take(60)
            .ToList();

        var miniCapture = new PcapCapture();
        miniCapture.Packets.AddRange(acePackets);
        miniCapture.LinkTypes.Add(1); // Ethernet

        var source = new ReplayAceFrameSource(miniCapture);
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;

        var service = new AceSnifferService(
            Options.Create(options), logger, lantable, _ => source);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        // Start capture
        await service.StartCaptureAsync();

        // STATE.md §25 flake fix: poll FramesCaptured==60 with a deadline instead
        // of a fixed Thread.Sleep(1000) — under parallel test load the replay may
        // take longer than 1 s to push all 60 frames through the frame channel
        // into the capture ring.
        await WaitForConditionAsync(
            () => service.GetCaptureStatus().FramesCaptured == 60,
            TimeSpan.FromSeconds(15),
            "capture did not accumulate all 60 frames");

        // Stop capture
        await service.StopCaptureAsync();

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Verify capture file exists and has content
        var captureStatus = service.GetCaptureStatus();
        Assert.False(captureStatus.Capturing);
        Assert.Equal(60, captureStatus.FramesCaptured);

        var path = service.GetCaptureFilePath();
        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"Capture file should exist at {path}");

        // Re-read the pcapng and verify frame count + linktype
        var reRead = PcapngReader.Read(path);
        Assert.Equal(60, reRead.Packets.Count);
        Assert.Contains(1, reRead.LinkTypes);

        // Cleanup
        try { File.Delete(path); } catch { }
    }

    // ═══ IsAceFrame validation ═══

    private static ReplayAceFrameSource DummySource()
    {
        var cap = new PcapCapture();
        cap.LinkTypes.Add(1);
        return new ReplayAceFrameSource(cap);
    }

    [Fact]
    public void ProcessFrame_RejectsNon152ByteFrames()
    {
        var lantable = Lantable;
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;
        var service = new AceSnifferService(
            Options.Create(options), logger, lantable,
            _ => DummySource());

        var decodedFrames = new List<(DateTime ts, uint eq, uint sv, IReadOnlyList<DecodedLanValue> signals)>();
        service.AceFrameDecoded += (ts, eq, sv, signals) =>
            decodedFrames.Add((ts, eq, sv, signals));

        // Build a frame with wrong length (140 instead of 152)
        var badFrame = BuildFrame(140, 0x03, 0x03, 0x08, 0x00A86F46, 0x13120206);
        service.ProcessFrame(badFrame);

        // Should increment totalFrames but NOT decode (wrong length)
        Assert.Empty(decodedFrames);
        Assert.Equal(1, service.GetStatus().TotalFrames);
    }

    [Fact]
    public void ProcessFrame_RejectsWrongPrefix()
    {
        var lantable = Lantable;
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;
        var service = new AceSnifferService(
            Options.Create(options), logger, lantable,
            _ => DummySource());

        var decodedFrames = new List<(DateTime ts, uint eq, uint sv, IReadOnlyList<DecodedLanValue> signals)>();
        service.AceFrameDecoded += (ts, eq, sv, signals) =>
            decodedFrames.Add((ts, eq, sv, signals));

        // Build a 152-byte frame with wrong LLC header (01 instead of 03 03 08/09)
        var badFrame = BuildFrame(152, 0x01, 0x02, 0x03, 0x00A86F46, 0x13120206);
        service.ProcessFrame(badFrame);

        Assert.Empty(decodedFrames);
    }

    [Fact]
    public void ProcessFrame_UnknownSv_IncrementsUnknownCounter()
    {
        var lantable = Lantable;
        var options = CreateOptions();
        var logger = NullLogger<AceSnifferService>.Instance;
        var service = new AceSnifferService(
            Options.Create(options), logger, lantable,
            _ => DummySource());

        // Build a valid-length, valid-prefix frame with an unknown sv (0xDEADBEEF)
        var frame = BuildFrame(152, 0x03, 0x03, 0x08, 0x00A86F46, 0xDEADBEEF);
        service.ProcessFrame(frame);

        var status = service.GetStatus();
        Assert.Equal(1, status.UnknownVectors);
        Assert.Equal(1, status.TotalFrames);

        // Frame should still count even though sv is unknown
        Assert.Equal(1, status.PrefixCounts["030308"]);
    }

    // ═══ Witness surface (2026-08-30: sniffer-witness gap) ═══

    [Fact]
    public void Witness_Counters_TrackAceFrames_DistinctMacs_EquipmentTotalFrames()
    {
        var service = new AceSnifferService(
            Options.Create(CreateOptions()), NullLogger<AceSnifferService>.Instance,
            Lantable, _ => DummySource());

        var macA = new byte[] { 0x00, 0x80, 0x2F, 0x23, 0xF4, 0x44 };
        var macB = new byte[] { 0x00, 0x80, 0x2F, 0x23, 0xF4, 0x55 };

        service.ProcessFrame(BuildFrameWithMac(macA, 0x00A86F46, 0x13120206));
        service.ProcessFrame(BuildFrameWithMac(macA, 0x00A86F46, 0x13120206));
        service.ProcessFrame(BuildFrameWithMac(macB, 0x00A86F46, 0x13120206));
        service.ProcessFrame(BuildFrame(140, 0x03, 0x03, 0x08, 0x00A86F46, 0x13120206)); // non-ACE (bad length)

        var status = service.GetStatus();
        Assert.Equal(4, status.TotalFrames);       // ALL frames including non-ACE
        Assert.Equal(3, status.AceFrames);         // ACE-validated only
        Assert.Equal(2, status.DistinctMacs);      // macA + macB
        // PrefixCounts counts ANY frame ≥17 B carrying the LLC prefix — the
        // 140-byte frame included (prefix tracking precedes IsAceFrame).
        Assert.Equal(4, status.PrefixCounts["030308"]);

        var eq = Assert.Single(status.Equipment);
        Assert.Equal(unchecked((int)0x00A86F46), eq.EquipmentNumber);
        Assert.Equal(3, eq.TotalFrames);           // per-equipment cumulative ACE frames
    }

    [Fact]
    public void WitnessLine_ShowsCounters_DeltaReset_AndEquipmentBreakdown()
    {
        var service = new AceSnifferService(
            Options.Create(CreateOptions()), NullLogger<AceSnifferService>.Instance,
            Lantable, _ => DummySource());

        var macA = new byte[] { 0x00, 0x80, 0x2F, 0x23, 0xF4, 0x44 };
        service.ProcessFrame(BuildFrameWithMac(macA, 0x00A86F46, 0x13120206));
        service.ProcessFrame(BuildFrameWithMac(macA, 0x00A86F46, 0x13120206));
        service.ProcessFrame(BuildFrameWithMac(macA, 0x00A86F46, 0x13120209));

        var now = DateTime.UtcNow;
        string first = service.BuildWitnessLine(now);
        Assert.Contains("witness:", first, StringComparison.Ordinal);
        Assert.Contains("ace=3 (+3/interval)", first, StringComparison.Ordinal);
        Assert.Contains("distinctMacs=1", first, StringComparison.Ordinal);
        Assert.Contains("0x00A86F46:3f", first, StringComparison.Ordinal);
        Assert.Contains("lastFrameAgo=", first, StringComparison.Ordinal);

        string second = service.BuildWitnessLine(now.AddSeconds(60));
        Assert.Contains("ace=3 (+0/interval)", second, StringComparison.Ordinal); // delta resets per interval
    }

    [Fact]
    public void Witness_Cadence_LogsAtMostOncePerInterval()
    {
        // The cadence constant is pinned so the witness line stays a per-minute
        // (not per-second) log citizen — 1 Hz stats loop × 60.
        Assert.Equal(60, AceSnifferService.WitnessIntervalSeconds);
    }

    // ═══ Helpers ═══

    /// <summary>
    /// Poll until <paramref name="condition"/> holds or the deadline expires
    /// (STATE.md §25: fixed sleeps flake under parallel test load — a bounded
    /// poll is deterministic regardless of machine speed).
    /// </summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"{what} within {timeout.TotalSeconds:0}s");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Wait until the sniffer's processed-frame counter stops moving for 500 ms
    /// (the replay is fully drained through the frame channel) — replaces the
    /// old fixed Thread.Sleep(2000) waits that flaked under parallel load.
    /// </summary>
    private static async Task WaitForReplaySettledAsync(AceSnifferService service)
    {
        long last = -1;
        var lastChange = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var now = service.GetStatus().TotalFrames;
            if (now != last)
            {
                last = now;
                lastChange = DateTime.UtcNow;
            }
            else if (now > 0 && DateTime.UtcNow - lastChange >= TimeSpan.FromMilliseconds(500))
            {
                return; // counter stable and non-empty — replay drained
            }
            await Task.Delay(50);
        }
        Assert.Fail($"replay did not settle within 15s (frames processed={last})");
    }

    /// <summary>BuildFrame with an explicit source MAC (witness tests need two
    /// distinct MACs to prove DistinctMacs counting).</summary>
    private static byte[] BuildFrameWithMac(byte[] srcMac, uint equipment, uint serviceVector)
    {
        var frame = BuildFrame(152, 0x03, 0x03, 0x08, equipment, serviceVector);
        Buffer.BlockCopy(srcMac, 0, frame, 6, 6);
        return frame;
    }

    /// <summary>
    /// Build a minimal ACE-like Ethernet frame for unit testing.
    /// Frame structure: 14-byte Ethernet header (dst MAC + src MAC + EtherType 0x00d0)
    /// + 3-byte LLC (b0 b1 b2) + padding to length, with equipment at offset 20 and sv at offset 24.
    /// </summary>
    private static byte[] BuildFrame(int length, byte llc0, byte llc1, byte llc2,
        uint equipment, uint serviceVector)
    {
        var frame = new byte[length];

        // Destination MAC (multicast: 03:00:01:00:00:20)
        frame[0] = 0x03; frame[1] = 0x00; frame[2] = 0x01;
        frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x20;

        // Source MAC (NI cRIO: 00:80:2f:23:f4:44)
        frame[6] = 0x00; frame[7] = 0x80; frame[8] = 0x2f;
        frame[9] = 0x23; frame[10] = 0xf4; frame[11] = 0x44;

        // EtherType 0x00d0 (proprietary)
        frame[12] = 0x00; frame[13] = 0xd0;

        // LLC header at offset 14
        frame[14] = llc0; frame[15] = llc1; frame[16] = llc2;

        // Padding bytes 17-19 (matching real capture: 00 d0 0d)
        frame[17] = 0x00; frame[18] = 0xd0; frame[19] = 0x0d;

        // Equipment number at offset 20 (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), equipment);

        // Service vector at offset 24 (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24, 4), serviceVector);

        // Fill remaining signal area with zeros
        for (int i = 28; i < length; i++)
            frame[i] = 0;

        return frame;
    }
}
