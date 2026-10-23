using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// LIVE end-to-end verification THROUGH the real Modbus surface (:502) —
/// layout v5 (bus register map v5):
///   iCemEmulator (emulation ON) → [iCem.exe AddIn] → persisted bus → station
///   ModbusTcpPublisher → FC03 over TCP → numbers in registers.
///
/// SELF-ORCHESTRATING GATE: the test itself brings the pipeline up —
///   1. iCemEmulator: if NOT running → emule_config.xml is patched (LanEnabled master
///      ∧ LanEmuleEnabled — the emitter opens ONLY on the master switch,
///      stand proof 2026-08-18) and the emulator is started; if ALREADY running → used as-is
///      (settings load at startup, so a running Emu with emulation OFF is
///      reported, never killed)
///   2. iCemPOC tray: if NOT running → started (station.ini modbus_on=1);
///      if ALREADY running → used as-is (Global\iCemDataMonitorTray_v1 guard)
///   3. iCem.exe (AddIn): NEVER started/killed by the test — it is the
///      operator's production app and the v5 header writer (regs 0..3 +
///      all zones); its absence is a VERDICT, not an orchestration target:
///      the gate raises a POPUP and waits up to 2 min for the operator to
///      launch it and enable recording (owner decree 2026-08-26; COM/LAN
///      emulation must be ON beforehand — the popup says so)
///   4. readiness barriers: bus file appears (v5: created by the AddIn — the
///      POC has no bus writer) → :502 accepts → warm-up for the first frames
///
/// Never kills a pre-existing (operator's) process. The ONLY process this
/// gate ever terminates is a iCemEmulator instance IT started this run, and only
/// inside ChannelDisable_StaleHold (settings load at startup make a runtime
/// channel disable require an Emu restart; pre-existing instances get a SKIP,
/// never a kill). iCem.exe is never touched, in any direction.
///
/// APP RENAME (plugin restructure 2026-08-26): the headless iCemEmule.exe is
/// replaced by the standalone WinUI+CLI app iCemEmulator.exe — the gate
/// launches THAT exe, and its WinUI window MAY appear on the stand during
/// live runs (accepted, documented here). The gate still kills only ITS OWN
/// instance (started via StartIfMissing, guarded by _emuStartedByGate), never
/// a pre-existing process. settings root element stays iCemEmule; the FILE
/// moved 2026-08-31 (owner decree) to %ProgramData%\Halliburton\iCem\
/// DataMonitoring\emule_config.xml — patched settings are honored at
/// startup; passive default = all OFF.
///
/// Category=Live: CI must exclude via `--filter Category!=Live` (no
/// Emu/desktop on CI agents). Synthetic per-layer coverage:
/// ModbusContractTests (temp bus, no OS apps).
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveModbusEmulatorTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 502;

    // App rename 2026-08-26: process name iCemEmulator. 2026-08-31 (owner
    // decree): settings file moved to ProgramData emule_config.xml (root
    // element stays iCemEmule) — PatchEmuSettings below.
    private const string EmuProcessName = "iCemEmulator";
    private const string HostProcessName = "iCemDataMonitor.Host";
    private const string IcemProcessName = "iCem";

    /// <summary>Build hint shown when the gate-owned exe is missing. Relative
    /// to the repo root (the assert already prints the absolute exe path).</summary>
    private const string EmuBuildHint =
        "build it once: dotnet build iCemEmulator\\iCemEmulator.csproj (repo root)";

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public LiveModbusEmulatorTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>Run the orchestration ONCE per test run (xUnit creates a class
    /// instance per test; the barrier Task deduplicates the bring-up).</summary>
    private static readonly Lazy<Task> Pipeline = new(() => EnsurePipelineAsync());

    /// <summary>True when THIS gate run launched iCemEmulator (it was absent at
    /// pipeline time). Only a gate-owned Emu may ever be restarted — an
    /// operator's pre-existing instance is never touched.</summary>
    private static volatile bool _emuStartedByGate;

    /// <summary>
    /// iCem.exe (AddIn host) absence = OPERATOR GATE, ¬ an orchestration
    /// target: the gate never starts iCem.exe. Instead it raises a POPUP and
    /// waits up to 2 minutes for the operator to launch iCem AND enable
    /// recording (COM/LAN acquisition in the iCem UI). The emulated feeds
    /// (COM or LAN) must be started BEFORE the launch — the popup reminds the
    /// operator; the gate itself patches the LAN/COM master switches for the
    /// emulator at bring-up (already-running instances are used as-is). iCem
    /// appearing within the window ⇒ proceed (the readiness barriers absorb
    /// the recording-enable state); timeout ⇒ SKIP with remediation.
    /// </summary>
    private static async Task WaitForIcemAsync()
    {
        if (Process.GetProcessesByName(IcemProcessName).Length > 0)
            return; // live already — no popup needed

        Console.WriteLine($"[gate] iCem.exe absent — operator popup, {IcemLaunchWindow.TotalSeconds:0}s window");
        using (OperatorPopup.Show(
            "ЖИВОЙ ТЕСТ: требуется iCem.exe и включённая запись.\r\n\r\n" +
            "Предварительно: эмуляция COM или LAN должна быть ВКЛЮЧЕНА (iCemEmulator).\r\n" +
            "1) Запустите iCem.exe (приложение с AddIn).\r\n" +
            "2) Включите запись / приём COM или LAN в UI iCem.\r\n\r\n" +
            $"Окно закроется автоматически: iCem найден или истекли {IcemLaunchWindow.TotalMinutes:0} мин.",
            "iCem Live Gate"))
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < IcemLaunchWindow)
            {
                if (Process.GetProcessesByName(IcemProcessName).Length > 0)
                {
                    Console.WriteLine($"[gate] iCem.exe detected after {sw.Elapsed.TotalSeconds:0}s");
                    return;
                }
                await Task.Delay(2000);
            }
        }

        throw Xunit.Sdk.SkipException.ForSkip(
            $"iCem.exe not started within {IcemLaunchWindow.TotalMinutes:0} min of the operator " +
            "prompt (popup shown) — absence is a verdict, not an orchestration target: the gate " +
            "never starts iCem.exe. Start COM/LAN emulation (iCemEmulator), launch iCem.exe with the " +
            "AddIn, enable recording, then re-run the Live gate.");
    }

    /// <summary>Operator window for the iCem launch + recording-enable popup
    /// gate (owner decree 2026-08-26: popup + 2-min timeout).</summary>
    private static readonly TimeSpan IcemLaunchWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Non-blocking informational popup (user32 MessageBoxW on a dedicated
    /// STA thread) — the gate polls iCem presence while it is visible and
    /// Dispose() closes it via WM_CLOSE when the gate resolves (found or
    /// timeout). Zero package dependencies: P/Invoke only, no WinForms.
    /// </summary>
    private sealed class OperatorPopup : IDisposable
    {
        private const uint MbIconInformation = 0x00000040;
        private const uint WmClose = 0x0010;

        private readonly string _caption;
        private readonly Thread _thread;

        public static OperatorPopup Show(string message, string caption)
        {
            var popup = new OperatorPopup(message, caption);
            return popup;
        }

        private OperatorPopup(string message, string caption)
        {
            _caption = caption;
            _thread = new Thread(() =>
            {
                _ = MessageBoxW(IntPtr.Zero, message, caption, MbIconInformation);
            });
            if (OperatingSystem.IsWindows())
                _thread.SetApartmentState(ApartmentState.STA); // user32 dialog needs STA; live gate is Windows-only
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Dispose()
        {
            // Gate resolved: close the dialog. A stray operator OK-click is
            // harmless — the thread simply exits.
            IntPtr hwnd = FindWindowW(null, _caption);
            if (hwnd != IntPtr.Zero)
                _ = PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// TEST 1 — First4Registers: regs 0..3 readable over Modbus; heartbeat
    /// (reg 3) advances (+1/s) whenever iCem + AddIn live — NO Streaming
    /// dependency (v5 paradigm: pipeline-alive signal; frozen ⇔ iCem/AddIn
    /// dead, NOT ⇔ ¬Streaming; wraps 32000→0). Mirror integrity: the Modbus
    /// words equal the DLL bus snapshot words (one publisher-tick grace).
    /// </summary>
    [Fact]
    public async Task First4Registers_Readable_HeartbeatReg3Advances()
    {
        await WaitForIcemAsync();
        await Pipeline.Value;

        using var bus = OpenBusReader();

        // (a) regs 0..3 readable + mirror the bus snapshot (grace re-read).
        var dll = BusSlice(bus, 0, BusLayout.HeaderRegCount);
        var modbus = await ReadRangeFc03Async(0, BusLayout.HeaderRegCount);
        if (!dll.SequenceEqual(modbus))
        {
            await Task.Delay(600); // one publisher tick grace (bus may advance between reads)
            dll = BusSlice(bus, 0, BusLayout.HeaderRegCount);
            modbus = await ReadRangeFc03Async(0, BusLayout.HeaderRegCount);
        }

        Assert.True(dll.SequenceEqual(modbus),
            $"regs 0..3 mirror MISMATCH: Modbus {string.Join(",", modbus)} != bus " +
            $"{string.Join(",", dll)} — publisher state-block mirror broken/stale");

        // (b) heartbeat reg 3: 5 s window, 400 ms sampling; PASS on the FIRST
        // observed change (u16 wrap 32000→0 counts as change), FAIL only if
        // static through the whole window. Writer = AddIn (iCem.exe), NOT the
        // POC; the v5 heartbeat is decoupled from Streaming.
        var (changed, samples) = await PollForChangeAsync(BusLayout.RegHeartbeat, 1, RangeWindow);

        Assert.True(changed,
            $"heartbeat reg {BusLayout.RegHeartbeat} static at {samples[0][0]} across the whole " +
            $"{RangeWindow.TotalSeconds:0}s window ({samples.Count} samples). " +
            "v5 paradigm: reg 3 advances +1/s ALWAYS while the AddIn is loaded " +
            "(pipeline-alive — iCem up ∧ POC reading ∧ Modbus publishing), NOT gated " +
            "on Streaming. Frozen ⇔ iCem/AddIn dead. Remediation: start iCem.exe with " +
            "the emulated feeds; the test launched Emu + POC but the AddIn is the " +
            "operator's app — it is never auto-started.");
    }

    /// <summary>
    /// TEST 2 — Block100_Mirror: reading holding registers in 100-register
    /// blocks across 0..599 mirrors the bus/DLL snapshot (coarse live
    /// doctrine: 5 s windows, 400 ms interval, PASS on the first change of
    /// ≥1 value, FAIL only if static all window). Per block: (1) mirror
    /// integrity — Modbus words == DLL snapshot; (2) liveness — blocks with a
    /// producer contract must change; (3) INC-06 dumb-translator scan on the
    /// lan_stream_crio blocks. The lan head block (200..299) IS the cRIO
    /// telemetry zone in v5 — it must be live when LAN emulation is on; raw
    /// blocks (400..599, float32 CDAB positional fields of the last CSV
    /// line) may be legitimately static and are reported, not failed.
    /// </summary>
    [Fact]
    public async Task Block100Mirror_ModbusEqualsBusSnapshot_Liveness()
    {
        await WaitForIcemAsync();
        await Pipeline.Value;

        using var bus = OpenBusReader();

        var failures = new List<string>();

        foreach (var block in Blocks)
        {
            // ── (0) zone activeFlag — the mirror verdict applies ONLY to
            //        active zones (A4/A5: inactive ⇒ publisher HOLD, the
            //        register file is not overwritten ⇒ mismatch = contract) ──
            var snap = bus.Snapshot();
            Assert.NotNull(snap);
            bool zoneActive = block.Base switch
            {
                < 200 => snap.ComMain.Active,
                < 400 => snap.Lan.Active,
                _ => snap.RawReserve.Active,
            };

            // ── (1) mirror integrity: DLL snapshot == Modbus words ──
            var dllWords = BusSlice(bus, block.Base, block.Length);
            var modbusWords = await ReadRangeFc03Async(block.Base, block.Length);
            bool mirrorOk = dllWords.SequenceEqual(modbusWords);
            if (!mirrorOk)
            {
                // one publisher-tick grace — the bus may have advanced between
                // the snapshot and the FC03 read; re-read BOTH once.
                await Task.Delay(600);
                dllWords = BusSlice(bus, block.Base, block.Length);
                modbusWords = await ReadRangeFc03Async(block.Base, block.Length);
                mirrorOk = dllWords.SequenceEqual(modbusWords);
            }

            // ── (2) liveness: 5 s change-detection on the Modbus surface ──
            var (changed, samples) = await PollForChangeAsync(block.Base, block.Length, RangeWindow);
            bool anyNonZero = samples.Any(s => s.Any(w => w != 0));

            _out.WriteLine($"[{block.Name}] regs {block.Base}..{block.Base + block.Length - 1}: " +
                           $"active={zoneActive} mirror={(mirrorOk ? "OK" : (zoneActive ? "MISMATCH" : "HOLD(A5)"))} " +
                           $"liveness={(changed ? "LIVE" : "static")} nonzero={anyNonZero} " +
                           $"samples={samples.Count}");

            if (!mirrorOk && zoneActive)
                failures.Add($"[{block.Name}] mirror MISMATCH while the zone is ACTIVE: Modbus words != " +
                             $"DLL bus snapshot (publisher mirror broken/stale for regs " +
                             $"{block.Base}..{block.Base + block.Length - 1})");
            if (block.ExpectLive && !changed)
                failures.Add($"[{block.Name}] static across {RangeWindow.TotalSeconds:0}s " +
                             $"({samples.Count} samples) but the block has an active producer contract. " +
                             $"Remediation: {block.LiveRemediation}");
            if (block.ExpectLive && !anyNonZero)
                failures.Add($"[{block.Name}] all zeros in every sample — the feed chain is dead " +
                             $"upstream of the bus. Remediation: {block.LiveRemediation}");

            // ── (3) INC-06 dumb-translator scan on the lan_stream_crio blocks ──
            if (block.ScanTruncation)
                ScanForTruncationSignature(modbusWords, block.Base, failures);
        }

        Assert.True(failures.Count == 0,
            "block100 matrix verdicts:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// TEST 3 — ChannelDisable_StaleHold (STALE-HOLD LAW, INV-23): disable
    /// the LAN channel in the emulator (gate-owned instance only) → the
    /// lan_stream_crio zone freezes at LAST values (NO zeroing), heartbeat
    /// keeps ticking; re-enable → values resume.
    ///
    /// Mechanics: iCemEmulator reads emule_config.xml at STARTUP only — a
    /// runtime channel disable therefore requires restarting the Emu with
    /// LanEmuleEnabled
    /// flipped. The gate does this ONLY for an Emu instance it started this
    /// run: the standalone WinUI app is gracefully close-attempted
    /// (CloseMainWindow) and, if it does not exit within the grace window
    /// (WinUI close semantics are not guaranteed), terminated; a PRE-EXISTING
    /// (operator's) instance ⇒ SKIP with reason, never touched.
    /// The POC tray and iCem.exe are never stopped in any case — the freeze
    /// must manifest from the AddIn's own channel-loss handling.
    /// </summary>
    [Fact]
    public async Task ChannelDisable_StaleHold_LanZoneFreezes_HeartbeatKeepsTicking_ReenableResumes()
    {
        await WaitForIcemAsync();
        await Pipeline.Value;

        if (!_emuStartedByGate)
            throw Xunit.Sdk.SkipException.ForSkip(
                "iCemEmulator was ALREADY running before this gate run (operator's instance) and " +
                "the gate never restarts/kills a pre-existing process. A runtime channel disable " +
                "needs an Emu restart (settings load at startup only). Re-run the gate on a stand " +
                "where the Emu is absent (the gate then owns it) or disable the LAN emulation " +
                "manually in the Emu's LAN tab.");

        var emuExe = ResolveEmuExe(FindRepoRoot());
        int lanBase = BusLayout.LanStreamCrioBaseReg;    // 200
        int lanLen = BusLayout.LanStreamCrioRegCount;    // 200

        // ── baseline: lan_stream_crio must be LIVE before the disable ──
        var baseline = await PollUntilChangeAsync(lanBase, lanLen, TimeSpan.FromSeconds(30));
        Assert.True(baseline.Changed,
            $"lan_stream_crio {lanBase}..{lanBase + lanLen - 1} never changed in 30 s — no " +
            "live baseline to freeze. Remediation: iCemEmulator LAN tab → emulation ON; iCem LAN " +
            "connection up; AddIn marking the Lan liveness block (D4).");
        ushort[] last = baseline.Last;
        Assert.True(last.Any(w => w != 0),
            "lan zone live (changing) but every sample all-zeros — the feed chain upstream " +
            "of the bus delivers no values. Check the Emu LAN emitter + iCem LAN link.");

        // ── disable: LAN emulation OFF + gate-owned Emu restart ──
        RestartGateOwnedEmu(emuExe, enableLanEmulation: false);

        // ── freeze detection: two samples 2 s apart equal ⇒ frozen at last values ──
        var frozen = await PollUntilFrozenAsync(lanBase, lanLen, TimeSpan.FromSeconds(30));
        Assert.True(frozen.Found,
            $"lan_stream_crio {lanBase}..{lanBase + lanLen - 1} did NOT freeze within 30 s after " +
            "the LAN channel disable — the AddIn keeps rewriting the zone (stale-hold law " +
            "INV-23 breach or feed loss not detected). Check the AddIn D4 liveness handling.");
        Assert.True(frozen.Words.Any(w => w != 0),
            "STALE-HOLD BREACH: the lan zone collapsed to all zeros after the channel " +
            "disable — zero-on-stop is FORBIDDEN (INV-23): last values must persist byte-exact.");

        // ── heartbeat keeps ticking while the zone is frozen (pipeline-alive) ──
        ushort hbBefore = (await ReadFc03Async(BusLayout.RegHeartbeat, 1))[0];
        await Task.Delay(2500);
        ushort hbAfter = (await ReadFc03Async(BusLayout.RegHeartbeat, 1))[0];
        Assert.NotEqual(hbBefore, hbAfter);

        // ── re-enable: LAN emulation ON + restart → values resume. The
        //    iCem↔Emu LAN link re-establishment lag after an Emu restart is
        //    up to ~2 min (stand proof 2026-08-18) — the window must cover it. ──
        RestartGateOwnedEmu(emuExe, enableLanEmulation: true);
        bool resumed = await PollForChangeFromAsync(lanBase, lanLen, frozen.Words, TimeSpan.FromSeconds(180));
        Assert.True(resumed,
            $"lan_stream_crio {lanBase}..{lanBase + lanLen - 1} did not resume within 180 s after " +
            "re-enabling the LAN channel. Remediation: Emu LAN emulation ON (restarted by the " +
            "gate), iCem LAN link re-established, AddIn re-marking the Lan liveness block (D4).");
    }

    /// <summary>Remediation pointer carried by every dumb-translator verdict
    /// (mandated operator text — do not reword).</summary>
    private const string DumbTranslatorRemediation =
        "foreign writer (iCem AddIn) violated dumb-translator law — see " +
        "specs/iCemAddIn/Э4_invariants.yaml + specs/incidents.csv INC-06; " +
        "POC writer is clean (BusWriteTruthTableTests)";

    /// <summary>
    /// Per-pair byte-level INC-06 scan over raw register words.
    /// Pair = (words[i], words[i+1]) at reg (baseAddress + i); the register
    /// file stores float32 in CDAB byte order ⇒ low word carries bytes [C D],
    /// high word [A B] (so ABCD float bits = A:hi&gt;&gt;8, B:hi&amp;0xFF,
    /// C:lo&gt;&gt;8, D:lo&amp;0xFF).
    /// </summary>
    private static void ScanForTruncationSignature(ushort[] words, int baseAddress, List<string> failures)
    {
        for (int i = 0; i + 1 < words.Length; i += 2)
        {
            ushort lo = words[i];
            ushort hi = words[i + 1];
            int address = baseAddress + i;
            string bytes = $"{(lo >> 8):X2} {(lo & 0xFF):X2} {(hi >> 8):X2} {(hi & 0xFF):X2}";

            // STRICT ARM — exact INC-06 wire signature: both 16-bit words have
            // their high byte zeroed while at least one low byte survives.
            if ((lo & 0xFF00) == 0 && (hi & 0xFF00) == 0 && (lo | hi) != 0)
            {
                failures.Add(
                    $"reg {address}: INC-06 truncation signature — raw bytes [{bytes}] " +
                    "(both 16-bit words have high byte 0x00, ≥1 low byte ≠ 0x00). " +
                    DumbTranslatorRemediation);
                continue; // one verdict per pair; the strict arm wins
            }

            // HEURISTIC ARM — denormal/near-denormal float = raw int bits in a
            // float slot. CDAB → ABCD float bits (see XML-doc above).
            uint bits = ((uint)(hi >> 8) << 24) | ((uint)(hi & 0xFF) << 16)
                      | ((uint)(lo >> 8) << 8) | (uint)(lo & 0xFF);
            int rawInt = unchecked((int)bits);
            float f = BitConverter.UInt32BitsToSingle(bits);
            float magnitude = Math.Abs(f);

            // Restriction rationale: (1) rawInt ∈ [1, 100_000_000] excludes
            // +0.0f (bits 0) and every sign-bit pattern (negative int32) ⇒
            // real zeros can never trip the heuristic; (2) truncation always
            // produces int-like bits 0x00yy00xx ∈ [65,537, 16,711,935] ⊂ band;
            // (3) normal engineering floats decode to int32 outside the band.
            if (magnitude > 1e-45f && magnitude < 1.2e-38f &&
                rawInt >= 1 && rawInt <= 100_000_000)
            {
                failures.Add(
                    $"reg {address}: denormal float signature — CDAB pair [{bytes}] decodes to " +
                    $"{f:E3} (|f| in (1e-45, 1.2e-38), raw int32 bits {rawInt}). " +
                    DumbTranslatorRemediation);
            }
        }
    }

    /// <summary>
    /// v5 bus register map blocks under observation (100-register blocks
    /// across 0..599). ExpectLive = an active producer contract exists for the
    /// block: blk000 carries heartbeat@3 (always ticking while the AddIn is
    /// loaded); blk200 = lan_stream_crio head — v5 note: the lan head IS the
    /// cRIO telemetry zone (per Wireshark/PROTOCOL.md), so it must be live
    /// when LAN emulation is on. blk300 (lan tail) liveness depends on the
    /// EMULATED SV SET — the stand's emulation lands its changing SVs below
    /// reg 300 (GUI run 2026-08-18: changes @222..299, tail static across 5 s
    /// while blk200 was LIVE) ⇒ report-only, NOT a contract. com_stream tail
    /// (blk100) and raw_com_line (blk400/blk500, float32 CDAB positional
    /// fields of the last CSV line) have no guaranteed producer in the
    /// pure-Emu topology ⇒ static is
    /// reported, not failed. ScanTruncation = INC-06 byte-level scan applies.
    ///
    /// MIRROR RULE (A4/A5 — GUI stand proof 2026-08-18): the mirror verdict
    /// (Modbus == bus snapshot) is asserted ONLY for blocks whose zone
    /// activeFlag is 1. An INACTIVE zone is NOT mirrored by the publisher
    /// (A5 hold — the register file keeps last/startup values while the AddIn
    /// may keep writing the bus side) ⇒ mismatch there is CONTRACT, not a
    /// defect (observed: raw=False with live ASCII in the bus raw zone).
    /// </summary>
    private static readonly BlockSpec[] Blocks =
    {
        new("blk000",   0, 100, ExpectLive: true,  ScanTruncation: false,
            "iCem.exe + AddIn loaded (heartbeat@3 is the AddIn's liveness word; " +
            "frozen ⇔ iCem/AddIn dead, NOT ⇔ ¬Streaming)."),
        new("blk100", 100, 100, ExpectLive: false, ScanTruncation: false, ""), // com_stream tail 100..199
        new("blk200", 200, 100, ExpectLive: true,  ScanTruncation: true,
            "lan_stream_crio head (200..299) = cRIO telemetry — v5: this IS the live " +
            "telemetry zone. iCemEmulator LAN tab → emulation ON (settings load at startup — " +
            "a running Emu is never killed; enable + restart it); iCem LAN link up; AddIn " +
            "marking the Lan liveness block (D4)."),
        new("blk300", 300, 100, ExpectLive: false, ScanTruncation: true, ""), // lan tail — liveness = SV-set property
        new("blk400", 400, 100, ExpectLive: false, ScanTruncation: false, ""), // raw_com_line float32 positional fields
        new("blk500", 500, 100, ExpectLive: false, ScanTruncation: false, ""), // raw_com_line positional fields tail
    };

    private sealed record BlockSpec(string Name, int Base, int Length, bool ExpectLive,
        bool ScanTruncation, string LiveRemediation);

    /// <summary>Range window per block: 5 s of 400 ms sampling (operator doctrine).</summary>
    private static readonly TimeSpan RangeWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>Two samples this far apart and byte-equal ⇒ the zone is frozen
    /// (feed loss settle allowance included in the caller's window).</summary>
    private static readonly TimeSpan FreezeProofSpan = TimeSpan.FromSeconds(2);

    /// <summary>Bus truth slice via the DLL snapshot (peer-reader law: the SAME
    /// BusReader the CLI uses; works alongside every station process).</summary>
    private static ushort[] BusSlice(BusReader bus, int address, int length)
    {
        var snap = bus.Snapshot();
        Assert.True(snap is not null, "DLL bus snapshot failed (no readable state)");
        Assert.True(snap.Words.Count >= address + length,
            $"DLL snapshot too small: {snap.Words.Count} regs < {address + length} required");
        var words = new ushort[length];
        snap.CopyWordsTo(address, words);
        return words;
    }

    /// <summary>Open the peer bus reader (same map the CLI uses) with the typed
    /// actionable exception surfaced for operator remediation.</summary>
    private static BusReader OpenBusReader()
    {
        var bus = new BusReader();
        try
        {
            Assert.True(bus.TryOpen(out var openError),
                $"DLL bus snapshot unavailable: {openError}");
        }
        catch (BusAccessDeniedException ex)
        {
            Assert.Fail($"DLL bus snapshot denied (typed, actionable):\n{ex.Message}");
        }
        return bus;
    }

    /// <summary>FC03 range read with ≤125-word chunking (Modbus spec limit).</summary>
    private static async Task<ushort[]> ReadRangeFc03Async(int address, int length)
    {
        var words = new ushort[length];
        int done = 0;
        while (done < length)
        {
            int chunk = Math.Min(125, length - done);
            var part = await ReadFc03Async(address + done, chunk);
            part.CopyTo(words, done);
            done += chunk;
        }
        return words;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Self-orchestration: Emu + POC up; already-running apps used as-is.
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task EnsurePipelineAsync()
    {
        var root = FindRepoRoot();

        // ── 1. iCemEmulator: emulation flag persisted BEFORE launch (settings
        //        load at startup); already-running instance = used as-is. ──
        PatchEmuSettings(enableLanEmulation: true);
        var emuExe = ResolveEmuExe(root);
        StartIfMissing(EmuProcessName, emuExe, EmuBuildHint);

        // ── 2. W20: legacy Tray is decommissioned — the Modbus :502 backend is
        //        now iCemDataMonitor.Host.exe (tray app). Already running ⇒
        //        single-instance guard (Global\iCemDataMonitorHost_v1) makes a
        //        duplicate exit 1; StartIfMissing skips it via process probe. ──
        // 2026-09-01 repo-restructure decree: FLAT layout — projects live in
        // the repo ROOT (no server-dotnet level), and the host dropped to
        // plain net10.0 with the H.NotifyIcon tray migration (Блок 2 п.4).
        var hostExe = Path.Combine(root, "iCemDataMonitor.Host", "bin", "Debug",
            "net10.0", "iCemDataMonitor.Host.exe");
        StartIfMissing(HostProcessName, hostExe,
            "build it once: dotnet build iCemDataMonitor.Host\\iCemDataMonitor.Host.csproj");

        // ── 3. readiness barriers ──
        await WaitForAsync(() => File.Exists(BusReader.DefaultMmfPath),
            TimeSpan.FromSeconds(60),
            $"bus file never appeared: {BusReader.DefaultMmfPath}. " +
            "v5: the file is created by the iCem AddIn (the POC has no bus writer) — " +
            "start iCem.exe with the AddIn loaded; the gate never starts it.");

        await WaitForAsync(TcpAccepts,
            TimeSpan.FromSeconds(60),
            $"Modbus :{Port} not accepting 60 s after bring-up. The tray either failed " +
            "startup (read its startup.log — FATAL lines are operator-actionable) or " +
            "modbus_on=0 in station.ini (ProgramData or next to iCemDataMonitor.Host.exe).");

        // First frames → zone writes: give the chain a warm-up window.
        await Task.Delay(3000);

        // ── LAN link readiness (stand proof 2026-08-18): after an Emu
        //    (re)start the iCem↔Emu LAN link takes up to ~2 min to
        //    establish — and after an iCem restart the LAN acquisition must
        //    be ENABLED in the iCem UI (the one manual stand step), so the
        //    window must absorb operator reaction time too. blk200/blk300
        //    liveness and the ChannelDisable baseline depend on CHANGING lan
        //    values. Wait for an actual change in the lan_stream_crio zone —
        //    ¬ a fixed sleep. OWNER DECREE W21: 60 s is ENOUGH — a silent
        //    300 s barrier looked like a hang; if the link is not up in a
        //    minute, the operator wants the actionable verdict NOW. ──
        await WaitForAsync(LanZoneChanging,
            TimeSpan.FromSeconds(60),
            "lan_stream_crio 200..399 never changed within 60 s after bring-up — " +
            "the iCem↔Emu LAN link did not come up. Remediation: iCemEmulator LAN tab → " +
            "enable (MASTER switch — the emitter opens only on LanEnabled) ∧ " +
            "emulation on; adapter = the 82574L NIC; then ENABLE LAN acquisition in " +
            "the iCem UI (required after every iCem restart — stand law 2026-08-18).");
    }

    /// <summary>
    /// True when the lan_stream_crio zone shows SUSTAINED change: TWO
    /// consecutive differing sample pairs (a≠b ∧ b≠c across 2×1.2 s). A
    /// one-off blip must NOT pass — the barrier false-fired on exactly that
    /// race: the orchestrator restarts iCem inside the barrier window and the
    /// dying AddIn's final flush changed the zone once, letting the tests run
    /// against a dead/booting iCem (stand proof 2026-08-18, scripted run).
    /// A live ACE feed (~0.5 Hz per SV, ≥8 SVs emulated) changes words in
    /// EVERY 1.2 s window. Peer BusReader — the CLI's surface; iCem's tap
    /// rewrites the zone with last-known values when the link is down
    /// (liveness marked, values frozen), so only CHANGE proves the feed.
    /// </summary>
    private static bool LanZoneChanging()
    {
        try
        {
            using var bus = new BusReader();
            if (!bus.TryOpen(out _))
                return false;
            var a = bus.Snapshot();
            if (a is null)
                return false;
            Thread.Sleep(1200);
            var b = bus.Snapshot();
            if (b is null)
                return false;
            Thread.Sleep(1200);
            var c = bus.Snapshot();
            if (c is null)
                return false;
            return LanDiffers(a, b) && LanDiffers(b, c);
        }
        catch
        {
            return false; // transient open/copy failure ⇒ not-ready, poll again
        }
    }

    private static bool LanDiffers(BusSnapshot x, BusSnapshot y)
    {
        int end = BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount;
        for (int r = BusLayout.LanStreamCrioBaseReg; r < end; r++)
            if (x.Words[r] != y.Words[r])
                return true;
        return false;
    }

    /// <summary>
    /// Stop ONLY the iCemEmulator instance this gate started, then relaunch it
    /// with the patched LAN-emulation flag. A pre-existing (operator's)
    /// instance is NEVER passed here — callers gate on _emuStartedByGate.
    /// iCemEmulator is a standalone WinUI app: a graceful CloseMainWindow is
    /// attempted first and, if the process does not exit within the grace
    /// window (WinUI close semantics are not guaranteed), the gate-owned
    /// process is terminated. The POC tray and iCem.exe are never stopped.
    /// </summary>
    private static void RestartGateOwnedEmu(string emuExe, bool enableLanEmulation)
    {
        var procs = Process.GetProcessesByName(EmuProcessName);
        if (procs.Length == 0)
            Assert.Fail("gate-owned iCemEmulator instance is gone — cannot restart it");

        var proc = procs[0];
        try { proc.CloseMainWindow(); } catch { /* hidden tray window */ }
        if (!proc.WaitForExit(5000))
        {
            proc.Kill(); // gate-owned instance ONLY (see class doc) — never an operator process
            proc.WaitForExit(10000);
        }

        PatchEmuSettings(enableLanEmulation);
        StartIfMissing(EmuProcessName, emuExe, EmuBuildHint);
    }

    /// <summary>
    /// Patch %ProgramData%\Halliburton\iCem\DataMonitoring\emule_config.xml
    /// (2026-08-31 owner decree: moved from the legacy %LOCALAPPDATA%\
    /// Halliburton\iCem\iCemEmule\settings.xml — the emulator copy-migrates
    /// the legacy file on first load): set the LAN channel flags, preserve
    /// every other element (additive schema — EmuSettings treats missing
    /// elements as defaults). Applies to the NEXT iCemEmulator start; a
    /// running instance is untouched (reported via verdicts).
    ///
    /// NOTE (app rename 2026-08-26): exe/process renamed iCemEmule →
    /// iCemEmulator; the settings ROOT ELEMENT stays iCemEmule (plugin
    /// contract) — patched settings are honored at startup;
    /// passive default = all OFF.
    /// </summary>
    private static void PatchEmuSettings(bool enableLanEmulation)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Halliburton", "iCem", "DataMonitoring");
        var path = Path.Combine(dir, "emule_config.xml");

        XElement rootEl;
        if (File.Exists(path))
        {
            try { rootEl = XDocument.Load(path).Root ?? new XElement("iCemEmule"); }
            catch { rootEl = new XElement("iCemEmule"); } // corrupt ⇒ fresh defaults
        }
        else
        {
            rootEl = new XElement("iCemEmule");
        }

        SetFlag(rootEl, "LanEnabled", enableLanEmulation);  // master: emitter open/close
        if (enableLanEmulation)
        {
            SetFlag(rootEl, "LanEmuleEnabled", true);       // values must vary for the live baseline
            // Owner decree 2026-08-26: «предварительно нужно запустить COM или
            // LAN» — a gate-owned emulator comes up with BOTH feeds on, so the
            // operator gate is satisfiable by either acquisition path.
            SetFlag(rootEl, "ComEnabled", true);
            SetFlag(rootEl, "ComEmuleEnabled", true);
        }

        Directory.CreateDirectory(dir);
        new XDocument(rootEl).Save(path);
    }

    private static void SetFlag(XElement rootEl, string name, bool value)
    {
        var el = rootEl.Element(name);
        if (el is null)
            rootEl.Add(new XElement(name, value));
        else
            el.Value = value.ToString();
    }

    private static void StartIfMissing(string processName, string exePath, string buildHint)
    {
        if (Process.GetProcessesByName(processName).Length > 0)
            return; // already running ⇒ use as-is; never duplicate, never kill

        Assert.True(File.Exists(exePath),
            $"{processName} not running and exe missing: {exePath}. {buildHint}");

        Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        if (processName == EmuProcessName)
            _emuStartedByGate = true;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        // W21 lesson (owner: «тесты зависают … нету действий никаких»): a
        // silent multi-minute barrier is indistinguishable from a hang. Announce
        // the wait and emit a heartbeat every 10 s until it resolves.
        Console.WriteLine($"[gate] waiting (up to {timeout.TotalSeconds:0}s): {failMessage.Split('.').First()}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        var nextBeat = TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                Console.WriteLine($"[gate] barrier cleared after {sw.Elapsed.TotalSeconds:0}s");
                return;
            }
            await Task.Delay(500);
            if (sw.Elapsed >= nextBeat)
            {
                Console.WriteLine($"[gate] still waiting… {sw.Elapsed.TotalSeconds:0}s / {timeout.TotalSeconds:0}s");
                nextBeat += TimeSpan.FromSeconds(10);
            }
        }
        Assert.Fail(failMessage);
    }

    private static bool TcpAccepts()
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(Host, Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Walk up from the test assembly dir to the repo root (the
    /// directory containing iCemEmulator\ and iCemDataMonitor.Host\).
    /// 2026-09-01 repo-restructure decree: flat layout — the server-dotnet
    /// marker is gone, the host project directory is the marker now.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Host")) &&
                (Directory.Exists(Path.Combine(dir.FullName, "iCemEmulator")) ||
                 Directory.Exists(Path.Combine(dir.FullName, "iCemEmule"))))
                return dir.FullName;
            dir = dir.Parent;
        }
        Assert.Fail($"repo root (iCemEmulator + iCemDataMonitor.Host) not found above {AppContext.BaseDirectory}");
        return null!; // unreachable
    }

    /// <summary>
    /// Resolve the standalone iCemEmulator exe (app rename 2026-08-26):
    /// primary = the plugin-agreed output layout
    /// iCemEmulator\bin\x64\Debug\net10.0-windows10.0.19041.0\iCemEmulator.exe;
    /// fallback = walk-up glob over iCemEmulator\bin\**\iCemEmulator.exe when
    /// the TFM dir differs (Debug ∧ x64 preferred). Returns the primary path
    /// (unresolved) when neither exists — StartIfMissing then raises the
    /// actionable build verdict.
    /// </summary>
    private static string ResolveEmuExe(string root)
    {
        var primary = Path.Combine(root, "iCemEmulator", "bin", "x64", "Debug",
            "net10.0-windows10.0.19041.0", "iCemEmulator.exe");
        if (File.Exists(primary))
            return primary;

        var binDir = Path.Combine(root, "iCemEmulator", "bin");
        if (Directory.Exists(binDir))
        {
            var best = Directory.GetFiles(binDir, "iCemEmulator.exe", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains("Debug", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                .ThenByDescending(p => p.Contains("x64", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();
            if (best is not null)
                return best;
        }

        return primary; // unresolved ⇒ StartIfMissing raises the actionable verdict
    }

    // ── minimal FC03 client (BCL-only; contract = ModbusTcpPublisher FC03) ──

    /// <summary>
    /// Sample the register range over the given window (chunked FC03 for
    /// ranges > 125 words). Returns changed ⇔ ANY word differed between ANY
    /// two consecutive samples (≥1 value moved ⇒ live, not static) plus the
    /// sample log for verdicts. Early exit on first change.
    /// </summary>
    private static async Task<(bool Changed, IReadOnlyList<ushort[]> Samples)> PollForChangeAsync(
        int address, int quantity, TimeSpan window)
    {
        var samples = new List<ushort[]> { await ReadRangeFc03Async(address, quantity) };
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            var next = await ReadRangeFc03Async(address, quantity);
            bool changed = !next.SequenceEqual(samples[^1]);
            samples.Add(next);
            if (changed)
                return (true, samples);
        }

        return (false, samples);
    }

    /// <summary>Poll until ANY change is observed; returns the last sample
    /// (baseline capture for the freeze phase).</summary>
    private static async Task<(bool Changed, ushort[] Last)> PollUntilChangeAsync(
        int address, int quantity, TimeSpan window)
    {
        ushort[] previous = await ReadRangeFc03Async(address, quantity);
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            var next = await ReadRangeFc03Async(address, quantity);
            if (!next.SequenceEqual(previous))
                return (true, next);
            previous = next;
        }

        return (false, previous);
    }

    /// <summary>Poll until the zone is FROZEN: a sample `a` followed by
    /// FreezeProofSpan of byte-equal samples (no writer activity). Returns the
    /// frozen words.</summary>
    private static async Task<(bool Found, ushort[] Words)> PollUntilFrozenAsync(
        int address, int quantity, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var a = await ReadRangeFc03Async(address, quantity);
            var stableUntil = DateTime.UtcNow + FreezeProofSpan;
            bool stable = true;
            while (DateTime.UtcNow < stableUntil)
            {
                await Task.Delay(PollInterval);
                var b = await ReadRangeFc03Async(address, quantity);
                if (!b.SequenceEqual(a))
                {
                    stable = false;
                    break;
                }
            }
            if (stable)
                return (true, a);
        }
        return (false, Array.Empty<ushort>());
    }

    /// <summary>Poll until ANY sample differs from the given baseline (resume
    /// detection after re-enable).</summary>
    private static async Task<bool> PollForChangeFromAsync(
        int address, int quantity, ushort[] baseline, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            var sample = await ReadRangeFc03Async(address, quantity);
            if (!sample.SequenceEqual(baseline))
                return true;
        }
        return false;
    }

    private static async Task<ushort[]> ReadFc03Async(int address, int quantity)
    {
        Assert.InRange(quantity, 1, 125); // Modbus application spec

        using var client = new TcpClient { ReceiveTimeout = 4000, SendTimeout = 4000 };
        await client.ConnectAsync(Host, Port);
        var stream = client.GetStream();

        // MBAP(txn=1, proto=0, len=6, unit=1) + PDU(FC03, addr, qty) — big-endian.
        byte[] req =
        {
            0, 1, 0, 0, 0, 6, 1, 0x03,
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(quantity >> 8), (byte)(quantity & 0xFF),
        };
        await stream.WriteAsync(req);

        var head = await ReadExactAsync(stream, 9); // 7 MBAP + func + byteCount
        byte func = head[7];
        int byteCount = head[8];
        var body = await ReadExactAsync(stream, byteCount);

        if ((func & 0x80) != 0)
            Assert.Fail($"FC03 exception from publisher: code={body[0]} at addr {address} qty {quantity}");

        var words = new ushort[quantity];
        for (int i = 0; i < quantity; i++)
            words[i] = (ushort)((body[i * 2] << 8) | body[i * 2 + 1]);
        return words;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read));
            if (n == 0)
                Assert.Fail($"Modbus connection closed mid-frame ({read}/{count} bytes)");
            read += n;
        }
        return buf;
    }
}