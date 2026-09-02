using System.Text.Json;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Host.CliMode;
using iCemDataMonitor.HostClient;

namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// CLI MODE thin-forwarder tests (W20 F.2 coverage, RELOCATED 2026-08-31 per
/// owner decree «пункт 3»: the iCemDataMonitor.Cli / Cli.Tests projects are
/// retired; the command logic moved VERBATIM into the host exe as
/// CliMode.CliForwarder and its tests came with it — same golden strings,
/// same argument table, same exit codes, same --start semantics; plus the
/// new argv-dispatcher truth table (cli tokens vs. host flags). The cli mode
/// is a remote control over the host pipe — zero bus logic.
/// </summary>
public sealed class CliForwarderTests
{
    // ═══ harness ═══

    private sealed class FakeApi : IHostApi
    {
        public HostStatus? Status;
        public Exception? Throw;

        public Task<HostStatus> GetStatusAsync(CancellationToken ct = default) =>
            Throw is null ? Task.FromResult(Status!) : Task.FromException<HostStatus>(Throw);

        public Task<RegistersReply> GetRegistersAsync(BusZoneKind zone, CancellationToken ct = default) =>
            Throw is null ? Task.FromResult(new RegistersReply()) : Task.FromException<RegistersReply>(Throw);

        public IMetricsStream SubscribeMetrics(int intervalMs) => throw new NotSupportedException();
        public Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HostStatus> SetAutostartAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();

        // W21.4: additive IHostApi members — the thin forwarder does not use
        // them yet (CLI hierarchy is a later wave); honest stubs keep the fake
        // compiling against the growing contract surface.
        public Task<HostStatus> SetConsoleVisibleAsync(bool visible, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NavigateUiResponse> NavigateUiAsync(UiSection section, CancellationToken ct = default) => throw new NotSupportedException();
        public IBootStageStream SubscribeBootStages() => throw new NotSupportedException();

        // 2026-08-31 (theme decree): additive IHostApi members — unused by the
        // CLI grammar today; honest stubs per the fake's own convention above.
        public Task<UiPrefs> GetUiPrefsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UiPrefs> SetUiThemeAsync(bool themeLight, CancellationToken ct = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private static RegistersReply Reply(params RegisterValue[] values)
    {
        var r = new RegistersReply { Heartbeat = 123 };
        r.Values.AddRange(values);
        return r;
    }

    private static (int Code, string Out, string Err) Run(
        string[] args,
        Func<TimeSpan, Task<bool>>? probe = null,
        IHostApi? api = null,
        string? resolvedExe = @"C:\h\iCemDataMonitor.Host.exe",
        List<string>? spawned = null,
        TimeSpan? startReadyTimeout = null,
        Func<string, bool>? signaler = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliForwarder.Run(args, stdout, stderr,
            pipeProbe: probe ?? (_ => Task.FromResult(true)),
            apiFactory: () => api ?? new FakeApi(),
            hostExeResolver: () => resolvedExe,
            hostSpawner: path => { spawned?.Add(path); return null; },
            startReadyTimeout: startReadyTimeout,
            eventSignaler: signaler ?? (_ => false));
        return (code, stdout.ToString().TrimEnd(), stderr.ToString());
    }

    // ═══ argv dispatcher truth table (2026-08-31 «пункт 3» merge seam) ═════
    // (InlineData rows are '|'-joined argv — attributes ban collection
    //  expressions; Split keeps every row a one-liner.)

    [Theory]
    [InlineData("snapshot")]
    [InlineData("header")]
    [InlineData("get|main")]
    [InlineData("--start|snapshot")]
    [InlineData("--start")] // cli-only flag: belongs to the cli grammar (→ usage error there)
    [InlineData("stop")]    // 2026-08-31 (слой CLI): lifecycle verbs are cli grammar
    [InlineData("restart")]
    public void Dispatcher_CliTokens_RouteToCliMode(string joined) =>
        Assert.True(CliForwarder.IsCliInvocation(joined.Split('|')));

    [Theory]
    [InlineData("")]                              // no args → station host
    [InlineData("--autostart")]                   // Run-key invocation
    [InlineData("--port|0")]
    [InlineData("--ini|x.ini")]
    [InlineData("--help")]                        // bare help → host help (with the cli section)
    [InlineData("--autostart|--port|8091")]
    public void Dispatcher_HostFlags_StayOnHostPath(string joined) =>
        Assert.False(CliForwarder.IsCliInvocation(
            joined.Length == 0 ? [] : joined.Split('|')));

    // ═══ formatters — golden strings (old contract shapes) ═══

    [Fact]
    public void FormatHeader_GoldenString()
    {
        var s = new HostStatus { ComPort = 10, CaseState = 3, CaseLanState = 5, Heartbeat = 123 };
        Assert.Equal("""{"comPortActive":1,"caseState":3,"caseLanState":5,"heartbeat":123}""",
            CliForwarder.FormatHeader(s));
    }

    [Fact]
    public void FormatHeader_ComPortZero_ActiveIsZero()
    {
        var s = new HostStatus { ComPort = 0, CaseState = 0, CaseLanState = 0, Heartbeat = 7 };
        Assert.Equal("""{"comPortActive":0,"caseState":0,"caseLanState":0,"heartbeat":7}""",
            CliForwarder.FormatHeader(s));
    }

    [Fact]
    public void FormatZoneFloats_GoldenString_TimestampPairReinterpreted()
    {
        // addr 4 = timestamp pair (int32 RAW BITS of 1.0f = 0x3F800000) — the
        // forwarder re-interprets the bits as float32 EXACTLY like the old
        // direct CDAB read; addr 6 = plain float 15.79.
        var reply = Reply(
            new RegisterValue { Addr = 4, Int32Value = unchecked((int)0x3F800000u) },
            new RegisterValue { Addr = 6, FloatValue = 15.79f },
            new RegisterValue { Addr = 8, FloatValue = 0f });
        Assert.Equal("[1,15.79,0]", CliForwarder.FormatZoneFloats(reply));
    }

    [Fact]
    public void FormatZoneFloats_NonFiniteSanitizedToZero()
    {
        var reply = Reply(new RegisterValue { Addr = 4, FloatValue = float.NaN });
        Assert.Equal("[0]", CliForwarder.FormatZoneFloats(reply));
    }

    [Fact]
    public void FormatSnapshotWords_GoldenString_HeaderPlusPairs()
    {
        // header words 10,3,5,123 (int32 singles) + timestamp pair 1.0f-bits
        // (low 0x0000, high 0x3F80) + float 15.79f (low 0xA3D7, high 0x417C).
        var reply = Reply(
            new RegisterValue { Addr = 0, Int32Value = 10 },
            new RegisterValue { Addr = 1, Int32Value = 3 },
            new RegisterValue { Addr = 2, Int32Value = 5 },
            new RegisterValue { Addr = 3, Int32Value = 123 },
            new RegisterValue { Addr = 4, Int32Value = unchecked((int)0x3F800000u) },
            new RegisterValue { Addr = 6, FloatValue = 15.79f });
        Assert.Equal("[10,3,5,123,0,16256,41943,16764]", CliForwarder.FormatSnapshotWords(reply));
    }

    // ═══ argument table ═══

    [Fact]
    public void NoCommand_Exit2()
    {
        var (code, json, err) = Run([]);
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Equal("", json);
        Assert.Contains("usage", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownCommand_Exit2_NamesTheCommand()
    {
        var (code, _, err) = Run(["frobnicate"]);
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Contains("frobnicate", err);
        Assert.Contains("usage", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("lan_ext")]
    [InlineData("wits")]
    [InlineData("bogus")]
    public void Get_UnknownZone_Exit2(string zone)
    {
        var (code, _, err) = Run(["get", zone]);
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Contains("usage", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtraPositional_Exit2()
    {
        var (code, _, _) = Run(["header", "extra"]);
        Assert.Equal(CliForwarder.ExitUsage, code);
    }

    [Fact]
    public void UnknownOption_Exit2()
    {
        var (code, _, err) = Run(["--path", "x", "header"]);
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Contains("--path", err);
    }

    [Fact]
    public void Help_Exit0_NothingOnStdout_NewExeNameInBanner()
    {
        var (code, json, err) = Run(["--help"]);
        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Equal("", json); // usage goes to stderr — stdout stays PURE JSON
        Assert.Contains("usage", err, StringComparison.OrdinalIgnoreCase);
        // 2026-08-31 («пункт 3»): the banner names the MERGED exe — the
        // icemdatamonitor-cli alias is retired with no stub.
        Assert.Contains("iCemDataMonitor.Host.exe", err);
        Assert.DoesNotContain("icemdatamonitor-cli", err);
    }

    // ═══ dead host / rpc failure exit codes ═══

    [Fact]
    public void DeadHost_Exit2_HonestPipeMessage_EmptyStdout()
    {
        var (code, json, err) = Run(["header"], probe: _ => Task.FromResult(false));
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Equal("", json);
        Assert.Contains("host not running", err);
        Assert.Contains(HostChannelNames.PipeName, err); // pipe name from Contracts, single source
    }

    [Fact]
    public void RpcFailure_AfterProbePassed_Exit1_NotMaskedAsDeadHost()
    {
        var api = new FakeApi { Throw = new InvalidOperationException("bus is not open") };
        var (code, json, err) = Run(["get", "main"], api: api);
        Assert.Equal(CliForwarder.ExitRpcFailed, code);
        Assert.Equal("", json);
        Assert.Contains("rpc failed", err);
        Assert.Contains("bus is not open", err);
        Assert.DoesNotContain("host not running", err);
    }

    [Fact]
    public void Header_RoundTrip_ThroughFakeApi()
    {
        var api = new FakeApi
        {
            Status = new HostStatus { ComPort = 2, CaseState = 1, CaseLanState = 2, Heartbeat = 42 },
        };
        var (code, json, _) = Run(["header"], api: api);
        Assert.Equal(CliForwarder.ExitOk, code);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1u, doc.RootElement.GetProperty("comPortActive").GetUInt16());
        Assert.Equal(42u, doc.RootElement.GetProperty("heartbeat").GetUInt16());
    }

    [Fact]
    public void Get_Main_MapsToComZone_FloatArrayOut()
    {
        BusZoneKind? seen = null;
        var api = new DelegatingApi
        {
            OnGetRegisters = z => { seen = z; return Reply(new RegisterValue { Addr = 4, FloatValue = 1.5f }); },
        };
        var (code, json, _) = Run(["get", "main"], api: api);
        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Equal(BusZoneKind.BusZoneCom, seen);
        Assert.Equal("[1.5]", json);
    }

    private sealed class DelegatingApi : IHostApi
    {
        public Func<BusZoneKind, RegistersReply> OnGetRegisters = _ => new RegistersReply();
        public Task<HostStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new HostStatus());
        public Task<RegistersReply> GetRegistersAsync(BusZoneKind zone, CancellationToken ct = default) =>
            Task.FromResult(OnGetRegisters(zone));
        public IMetricsStream SubscribeMetrics(int intervalMs) => throw new NotSupportedException();
        public Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HostStatus> SetAutostartAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();

        // W21.4: additive IHostApi members (unused by the thin forwarder yet).
        public Task<HostStatus> SetConsoleVisibleAsync(bool visible, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NavigateUiResponse> NavigateUiAsync(UiSection section, CancellationToken ct = default) => throw new NotSupportedException();
        public IBootStageStream SubscribeBootStages() => throw new NotSupportedException();

        // 2026-08-31 (theme decree): additive IHostApi members (unused by the
        // CLI grammar today); honest stubs per the fake's own convention above.
        public Task<UiPrefs> GetUiPrefsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UiPrefs> SetUiThemeAsync(bool themeLight, CancellationToken ct = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    // ═══ --start semantics ═══

    [Fact]
    public void StartFlag_HostComesUp_SpawnsOnce_ThenExecutes()
    {
        bool[] probes = [false, true];
        int i = 0;
        var spawned = new List<string>();
        var api = new FakeApi { Status = new HostStatus { ComPort = 1, Heartbeat = 5 } };

        var (code, json, err) = Run(["--start", "header"],
            probe: _ => Task.FromResult(probes[Math.Min(i++, probes.Length - 1)]),
            api: api,
            spawned: spawned);

        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Single(spawned);
        Assert.Equal(@"C:\h\iCemDataMonitor.Host.exe", spawned[0]);
        Assert.Contains("READY", err);
        Assert.Contains("comPortActive", json);
    }

    [Fact]
    public void StartFlag_TimeoutNoReady_Exit2()
    {
        var spawned = new List<string>();
        // W22: the READY budget is injectable — the timeout path is exercised
        // in ~2 s instead of burning the real 30 s production budget.
        var (code, json, err) = Run(["--start", "snapshot"],
            probe: _ => Task.FromResult(false),
            spawned: spawned,
            startReadyTimeout: TimeSpan.FromSeconds(2));
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Equal("", json);
        Assert.Single(spawned); // spawn attempted, READY never came
        Assert.Contains("READY", err);
        Assert.Contains("within 2 s", err);
        Assert.Contains("host not running", err);
    }

    [Fact]
    public void StartFlag_ExeNotFound_Exit2_SpawnerNeverCalled()
    {
        var spawned = new List<string>();
        var (code, _, err) = Run(["--start", "header"],
            probe: _ => Task.FromResult(false),
            resolvedExe: null,
            spawned: spawned);
        Assert.Equal(CliForwarder.ExitUsage, code);
        Assert.Empty(spawned);
        Assert.Contains("not found", err);
    }

    // ═══ 2026-08-31 (слой CLI «n+1 источник команд»): lifecycle verbs ═══
    // The forwarder only POKES the host-owned events; the host executes the
    // verb itself through HostLifecycle. stdout stays PURE JSON.

    [Fact]
    public void Stop_SignalsShutdownEvent_Exit0_PureJson()
    {
        var signalled = new List<string>();
        var (code, json, _) = Run(["stop"], signaler: name => { signalled.Add(name); return true; });
        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Equal([iCemDataMonitor.Host.Tray.CrossProcessNames.HostShutdownEvent], signalled);
        Assert.Equal("""{"action":"stop","result":"signalled"}""", json);
    }

    [Fact]
    public void Stop_HostDead_Idempotent_Exit0_NotRunning()
    {
        var signalled = new List<string>();
        var (code, json, err) = Run(["stop"], signaler: name => { signalled.Add(name); return false; });
        Assert.Equal(CliForwarder.ExitOk, code); // idempotent — a dead host IS stopped
        Assert.Equal("""{"action":"stop","result":"notRunning"}""", json);
        Assert.Contains("idempotent", err);
        Assert.Single(signalled);
    }

    [Fact]
    public void Restart_SignalsRestartEvent_WaitsReady_PureJson()
    {
        var signalled = new List<string>();
        // probe answers immediately (successor READY on the first poll after
        // the settle delay) — the wait arm completes without real waiting.
        var (code, json, err) = Run(["restart"],
            signaler: name => { signalled.Add(name); return true; });
        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Equal([iCemDataMonitor.Host.Tray.CrossProcessNames.HostRestartEvent], signalled);
        Assert.Equal("""{"action":"restart","result":"restarted"}""", json);
        Assert.Contains("READY", err);
    }

    [Fact]
    public void Restart_DeadHost_SpawnsAsStart_SystemctlSemantics()
    {
        var signalled = new List<string>();
        var spawned = new List<string>();
        bool[] probes = [false, true];
        int i = 0;
        var (code, json, _) = Run(["restart"],
            probe: _ => Task.FromResult(probes[Math.Min(i++, probes.Length - 1)]),
            spawned: spawned,
            signaler: name => { signalled.Add(name); return false; });
        Assert.Equal(CliForwarder.ExitOk, code);
        Assert.Single(spawned); // restart on a dead host = start
        Assert.Equal("""{"action":"restart","result":"spawned"}""", json);
        Assert.Single(signalled); // the event poke was TRIED first — no blind spawn
    }

    [Fact]
    public void Restart_EventMissing_ButPipeAnswers_Exit1_OldBuildHonesty()
    {
        // Transition state on a stand: the OLD host build answers the pipe
        // but never created the restart event — spawning a twin would be a
        // lie; the operator restarts it once from the tray instead.
        var spawned = new List<string>();
        var (code, json, err) = Run(["restart"],
            probe: _ => Task.FromResult(true),
            spawned: spawned,
            signaler: _ => false);
        Assert.Equal(CliForwarder.ExitRpcFailed, code);
        Assert.Equal("", json);
        Assert.Empty(spawned);
        Assert.Contains("NO restart event", err);
    }

    [Fact]
    public void Restart_ExtraSubcommand_Exit2() =>
        Assert.Equal(CliForwarder.ExitUsage, Run(["restart", "now"]).Code);

    [Fact]
    public void FormatAction_GoldenStrings()
    {
        Assert.Equal("""{"action":"stop","result":"signalled"}""",
            CliForwarder.FormatAction("stop", "signalled"));
        Assert.Equal("""{"action":"restart","result":"restarted"}""",
            CliForwarder.FormatAction("restart", "restarted"));
    }
}
