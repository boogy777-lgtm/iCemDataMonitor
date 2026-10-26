// EmulatorAppTests — 2026-08-27 owner decree «эмулятор = отдельное
// независимое приложение со встроенным UI + CLI, своя папка»: pins the
// MERGED app's seams that live in iCemEmule.Core (testable without WinUI):
//   1) command-line mode parsing (default = UI mode; --cli; --selftest;
//      --landiag) — EmulatorAppArgs;
//   2) the cross-zone identity pins the transport live gate depends on
//      (exe/process name, deploy folder, single-instance mutex);
//   3) the single-instance mutex contract (second acquire fails);
//   4) the --cli command table AGAINST THE IN-PROCESS ENGINE over the control
//      pipe (unique pipe name — the live engine on the stand is never
//      touched; same grammar as CommandProcessorParityTests);
//   5) emule_config.xml honored at startup: explicit enables auto-start,
//      unpatched defaults stay fully passive.
// EmulatorAppTests + InProcControlChannelTests share the PROCESS-GLOBAL
// statics (EmuSettings.SettingsPathOverride, ControlPipeClient.LocalExecutor)
// — the collection serializes them (P5 2026-08-28; a parallel class's
// SendCommand otherwise gets routed to the other class's executor).

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;
using Xunit;

namespace iCemEmule.Tests;

[Collection("EmulatorControlStatics")]
public class EmulatorAppTests
{
    // ── (1) mode parsing ─────────────────────────────────────────────────

    [Fact]
    public void NoArgs_IsDefaultUiMode()
    {
        Assert.Equal(EmulatorAppMode.Ui, EmulatorAppArgs.Parse(Array.Empty<string>(), out var rest));
        Assert.Empty(rest);
    }

    [Fact]
    public void UnknownArgs_StillOpenTheUi_DefaultUiLaw()
    {
        // Default-UI law: garbage/unknown flags never crash the app — the
        // window opens (the old headless exe's flag surface is gone).
        Assert.Equal(EmulatorAppMode.Ui, EmulatorAppArgs.Parse(new[] { "--frobnicate" }, out _));
        Assert.Equal(EmulatorAppMode.Ui, EmulatorAppArgs.Parse(new[] { "--ctl" }, out _));
    }

    [Fact]
    public void CliFlag_ParsesMode_AndRestTravelsWithTheCommand()
    {
        Assert.Equal(EmulatorAppMode.Cli,
            EmulatorAppArgs.Parse(new[] { "--cli", "status" }, out var rest));
        Assert.Equal(new[] { "status" }, rest);

        Assert.Equal(EmulatorAppMode.Cli,
            EmulatorAppArgs.Parse(new[] { "--cli", "set", "\"Pump A Pressure\"", "5000" }, out rest));
        Assert.Equal(new[] { "set", "\"Pump A Pressure\"", "5000" }, rest);
    }

    [Fact]
    public void SelftestAndLanDiag_ModesParse()
    {
        Assert.Equal(EmulatorAppMode.SelfTest, EmulatorAppArgs.Parse(new[] { "--selftest" }, out _));
        Assert.Equal(EmulatorAppMode.LanDiag,
            EmulatorAppArgs.Parse(new[] { "--landiag", "82574L" }, out var rest));
        Assert.Equal(new[] { "82574L" }, rest);
    }

    // ── (2)+(3) identity pins + single-instance contract ─────────────────

    [Fact]
    public void AppContract_PinsTheCrossZoneIdentity()
    {
        // The transport live gate's pins (LiveModbusEmulatorTests in the
        // transport zone) — an accidental rename breaks the STAND, not the build.
        Assert.Equal("iCemEmulator", EmulatorAppContract.ProcessName);
        Assert.Equal("iCemEmulator.exe", EmulatorAppContract.ExeFileName);
        Assert.Equal("iCemEmulator", EmulatorAppContract.DeployFolderName);
        Assert.Equal(
            @"C:\Program Files\Halliburton\iCemMonitor\iCemEmulator",
            EmulatorAppContract.DeployFolderPath);
        Assert.Equal(@"Global\iCemEmulator_v1", EmulatorAppContract.SingleInstanceMutexName);
    }

    [Fact]
    public void SingleInstanceMutex_SecondAcquireFails_WhileFirstIsHeld()
    {
        using var first = new Mutex(true, EmulatorAppContract.SingleInstanceMutexName, out bool createdNew);
        Assert.True(createdNew, "first acquire must own the mutex");

        using var second = new Mutex(true, EmulatorAppContract.SingleInstanceMutexName, out bool secondCreated);
        Assert.False(secondCreated, "a second app instance must NOT acquire the mutex");
    }

    // ── (4) --cli command table against the in-process engine ────────────

    /// <summary>Minimal fakes — no hardware; the engine's own contracts.</summary>
    private sealed class FakeComEmitter : IComEmitter
    {
        public bool IsOpen { get; private set; }
        public string? PortName { get; private set; }
        public long LinesSent { get; private set; }
        public List<string> OpenCalls { get; } = new();
        public event Action<string>? StatusChanged { add { } remove { } }
        public string Open(string portName) { OpenCalls.Add(portName); IsOpen = true; PortName = portName; return ""; }
        public void SendLine(string line) => LinesSent++;
        public void Close() { IsOpen = false; PortName = null; }
        public void Dispose() => Close();
    }

    private sealed class FakeLanEmitter : IAceLanEmitter
    {
        public bool IsOpen { get; private set; }
        public string? AdapterName { get; private set; }
        public long FramesSent { get; private set; }
        public List<string> OpenCalls { get; } = new();
        public event Action<string>? StatusChanged { add { } remove { } }
        public static IReadOnlyList<string> ListAdapters() => new[] { "fake0" };
        public string Open(string adapterDescriptionOrName) { OpenCalls.Add(adapterDescriptionOrName); IsOpen = true; AdapterName = adapterDescriptionOrName; return ""; }
        public void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac)
            => FramesSent += vectors.Count;
        public void Close() { IsOpen = false; AdapterName = null; }
        public void Dispose() => Close();
    }

    /// <summary>
    /// Starts an in-proc engine on a UNIQUE pipe and redirects settings
    /// persistence to a temp file — Stop() saves settings, and a test must
    /// NEVER clobber the stand's (possibly transport-gate-patched)
    /// %ProgramData%\Halliburton\iCem\DataMonitoring\emule_config.xml
    /// (2026-08-31: canonical path moved from %LOCALAPPDATA% settings.xml).
    /// </summary>
    private static (EmuEngineHost Host, string PipeName, FakeComEmitter Com, FakeLanEmitter Lan, string? PrevOverride)
        StartEngine(EmuSettings settings)
    {
        var prev = EmuSettings.SettingsPathOverride;
        EmuSettings.SettingsPathOverride = Path.Combine(Path.GetTempPath(),
            "iCemEmule.Tests." + Guid.NewGuid().ToString("N"), "emule_config.xml");
        var com = new FakeComEmitter();
        var lan = new FakeLanEmitter();
        var pipeName = "iCemEmule.Tests." + Guid.NewGuid().ToString("N");
        var host = new EmuEngineHost(settings, com, lan, pipeName);
        host.Start();
        return (host, pipeName, com, lan, prev);
    }

    [Fact]
    public void CliCommandTable_AgainstInProcEngine_RoundTripsOnThePipe()
    {
        var (host, pipe, _, _, prev) = StartEngine(new EmuSettings());
        try
        {
            // status — the one-shot `--cli status` reply the smoke test prints.
            using (var status = ControlPipeClient.SendCommand("{\"cmd\":\"status\"}", pipe))
            {
                Assert.NotNull(status);
                Assert.True(status!.Ok, "status must be ok against the in-proc engine");
                var root = status.Raw.RootElement;
                Assert.True(root.GetProperty("data").TryGetProperty("proto", out var proto) &&
                    proto.GetInt32() >= 1, "status must report the pipe protocol version");
                Assert.False(root.GetProperty("data").GetProperty("com").GetProperty("streaming").GetBoolean(),
                    "passive defaults: com must NOT stream");
                Assert.False(root.GetProperty("data").GetProperty("lan").GetProperty("streaming").GetBoolean(),
                    "passive defaults: lan must NOT stream");
            }

            // start with no port = the EXACT mapped refusal (UI/CLI contract).
            using (var refused = ControlPipeClient.SendCommand("{\"cmd\":\"start\",\"channel\":\"com\"}", pipe))
            {
                Assert.NotNull(refused);
                Assert.False(refused!.Ok);
                Assert.Equal(EmuEngineHost.ErrNoComPort, refused.Error);
            }

            // profiles + vars + ports — the CLI read commands the UI uses.
            foreach (var cmd in new[] { "profiles", "vars", "ports", "adapters" })
            {
                using var reply = ControlPipeClient.SendCommand("{\"cmd\":\"" + cmd + "\"}", pipe);
                Assert.NotNull(reply);
                Assert.True(reply!.Ok, $"'{cmd}' must be ok against the in-proc engine");
            }

            // quit — the pipe must unblock WaitForQuit (the app's exit path).
            using (var quit = ControlPipeClient.SendCommand("{\"cmd\":\"quit\"}", pipe))
            {
                Assert.NotNull(quit);
                Assert.True(quit!.Ok, "quit must ACK");
                Assert.True(quit.Raw.RootElement.GetProperty("data").GetProperty("quitting").GetBoolean());
            }
            Assert.True(WaitForQuit(host), "quit must unblock WaitForQuit");
        }
        finally
        {
            host.Stop();
            EmuSettings.SettingsPathOverride = prev;
        }
    }

    private static bool WaitForQuit(EmuEngineHost host)
    {
        var task = Task.Run(() => host.WaitForQuit());
        return task.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Flaky-under-load guard (known pipe-timing races): StartEngine
    /// binds the pipe inside Start(); under full-suite load the first
    /// SendCommand can lose the bind race and return null. Bounded retry —
    /// the assertion semantics (streaming/emulating true) are unchanged.</summary>
    private static ControlPipeClient.Reply? RetryStatus(string pipe, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            var reply = ControlPipeClient.SendCommand("{\"cmd\":\"status\"}", pipe);
            if (reply is not null)
                return reply;
            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(100);
        }
    }

    // ── (5) emule_config.xml honored at startup ─────────────────────────

    [Fact]
    public void SettingsHonoredAtStartup_ExplicitEnables_AutoStartBothChannelsAndEmulation()
    {
        // The transport live gate patches emule_config.xml BEFORE launching
        // the app and relies on it being honored at startup.
        var settings = new EmuSettings
        {
            ComEnabled = true, ComPort = "COM9", ComEmuleEnabled = true,
            LanEnabled = true, LanAdapter = "fake0", LanEmuleEnabled = true,
        };
        var (host, pipe, com, lan, prev) = StartEngine(settings);
        try
        {
            Assert.True(com.IsOpen, "COM emitter must open the configured port at startup");
            Assert.Equal("COM9", Assert.Single(com.OpenCalls));
            Assert.True(lan.IsOpen, "LAN emitter must open the configured adapter at startup");
            Assert.Equal("fake0", Assert.Single(lan.OpenCalls));

            using var status = RetryStatus(pipe, TimeSpan.FromSeconds(5));
            Assert.NotNull(status);
            var data = status!.Raw.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("com").GetProperty("streaming").GetBoolean());
            Assert.True(data.GetProperty("com").GetProperty("emulating").GetBoolean());
            Assert.True(data.GetProperty("lan").GetProperty("streaming").GetBoolean());
            Assert.True(data.GetProperty("lan").GetProperty("emulating").GetBoolean());
        }
        finally
        {
            host.Stop();
            EmuSettings.SettingsPathOverride = prev;
        }
    }

    [Fact]
    public void UnpatchedSettings_StayFullyPassive_EvenAfterStart()
    {
        // Passive-start defaults: a clean install (no explicit enables) must
        // NEVER bind a port or start a stream on its own.
        var (host, _, com, lan, prev) = StartEngine(new EmuSettings());
        try
        {
            Assert.Empty(com.OpenCalls);
            Assert.Empty(lan.OpenCalls);
            var st = host.GetStatus();
            Assert.False((bool)((Dictionary<string, object?>)st["com"]!)["streaming"]!);
            Assert.False((bool)((Dictionary<string, object?>)st["lan"]!)["streaming"]!);
        }
        finally
        {
            host.Stop();
            EmuSettings.SettingsPathOverride = prev;
        }
    }
}
