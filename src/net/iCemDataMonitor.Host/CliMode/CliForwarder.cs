using System.Diagnostics;
using System.Text.Json;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.HostClient;
using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Host.CliMode;

/// <summary>
/// CLI MODE of the host exe — THIN FORWARDER (W20 F.2, owner decree
/// «спина к спине»; MOVED verbatim from the retired iCemDataMonitor.Cli
/// project). Every command is one RPC over the host named pipe
/// (Contracts.HostChannelNames.PipeName). NO bus/MMF access — the CLI mode
/// never touches Data.dat itself.
///
/// 2026-08-31 (owner decree «пункт 3»): the separate icemdatamonitor-cli.exe
/// (and its 51.9 MB self-contained cli\ install folder) is RETIRED. The CLI
/// now lives INSIDE iCemDataMonitor.Host.exe: WITH cli arguments
/// (snapshot|get|header [--start]) the exe runs the command and exits BEFORE
/// any host machinery (no tray, no Kestrel, no single-instance mutex);
/// WITHOUT them it is the station host exactly as before. There is NO stub
/// exe — scripts/shortcuts must call the new form:
///   iCemDataMonitor.Host.exe snapshot|get|header [--start]
///
/// Machine-to-machine I/O contract (UNCHANGED shapes — scripts depend on them):
///   stdout = PURE JSON, single line — nothing else ever touches stdout
///   stderr = logs/diagnostics only
///   exit codes: 0 = ok
///               1 = host answered but the RPC failed (e.g. bus not open)
///               2 = usage error OR host not running / not READY in time
///
/// Commands → RPC → stdout shape:
///   header            → GetStatus        → {"comPortActive":N,"caseState":N,
///                                           "caseLanState":N,"heartbeat":N}
///                        (comPortActive = status.ComPort > 0 ? 1 : 0)
///   get main|lan|raw  → GetRegisters(COM/LAN/RAW) → JSON array of floats,
///                        the OLD get format (main 98, lan/raw 100 values;
///                        the timestamp int32 pair at addr 4 re-interprets its
///                        raw bits as float32 exactly like the old CDAB read)
///   snapshot          → GetRegisters(UNSPECIFIED) → JSON array of u16 words
///                        (the OLD snapshot form; live window 0..599 — see
///                        DEVIATION note in the W20 F.2 report)
///
/// 2026-08-31 (слой CLI, owner decree «закрой пробел CLI — n+1 источник
/// команд для управления ядром»): LIFECYCLE verbs — the CLI is a FIRST-CLASS
/// command source next to tray/UI/web. They never touch the process directly:
/// they poke the SAME host-owned named events the shell button uses, and the
/// host executes the verb ITSELF through HostLifecycle (E.2.7 intact):
///   stop     → SET HostShutdownEvent → host runs RequestClose (the tray
///              [Закрыть] path). Idempotent: a dead host is a success with
///              {"action":"stop","result":"notRunning"}.
///   restart  → SET HostRestartEvent → host runs RequestRestart (the tray
///              [Перезапуск] path) → wait for the successor READY ≤30 s →
///              {"action":"restart","result":"restarted"}. Dead host ⇒
///              restart = start (systemctl semantics): the SAME spawn path as
///              --start → {"action":"restart","result":"spawned"}.
///
/// Flags:
///   --start   when the host pipe does not answer: spawn iCemDataMonitor.Host.exe
///             (resolver sibling → host\ → dev-walk-up — with the merge the
///             sibling candidate IS this very exe, so --start re-launches the
///             same binary in host mode) and wait for READY on the pipe ≤30 s,
///             then run the command. NO local fallback — a dead host is an
///             honest exit 2.
/// </summary>
internal static class CliForwarder
{
    internal const int ExitOk = 0;
    internal const int ExitRpcFailed = 1;
    internal const int ExitUsage = 2;

    /// <summary>How long --start waits for the spawned host to answer the pipe.</summary>
    internal static readonly TimeSpan StartReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Single probe window used to decide "host running" up front.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 2026-08-31 (owner decree «пункт 3») argv dispatcher predicate — the
    /// FIRST classification the merged exe performs. A token set belongs to
    /// the CLI grammar when it carries a cli command word (snapshot / get /
    /// header) or the cli-only --start flag. The host's own flags
    /// (--autostart / --port / --ini / --help) never collide with these
    /// tokens, so host invocations fall through to the host path untouched.
    /// </summary>
    internal static bool IsCliInvocation(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "snapshot" or "get" or "header" or "--start" or "stop" or "restart")
                return true;
        }
        return false;
    }

    /// <summary>Cli-mode entry: called from Program.cs when IsCliInvocation
    /// classifies argv into the cli grammar. NOT named "Main" — the merged
    /// exe's real entry point is the top-level program (CS7022).</summary>
    internal static int CliMain(string[] args)
    {
        // W23 (owner log policy 2026-08-26): fatal/unhandled errors land in the
        // station log via StationFileLog — never-throw, 25 MB cap with in-place
        // tail-half rewrite, lazy directory creation. 2026-08-31 («пункт 3»):
        // the log folder KEEPS the historical iCemDataMonitor.Cli name — docs
        // and operator runbooks reference Logs\iCemDataMonitor.Cli\cli.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal($"AppDomain.UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Fatal($"UnobservedTaskException: {e.Exception}");
            e.SetObserved(); // evidence is on file — keep the default no-crash behavior
        };

        try
        {
            return Run(args, Console.Out, Console.Error);
        }
        catch (Exception ex)
        {
            // Run() maps every CONTRACTED failure to an exit code; reaching this
            // handler = a defect outside the contract. Honest stderr + exit 1
            // (the "error" bucket of the documented 0/1/2 machine contract).
            Fatal($"fatal: {ex}");
            try { Console.Error.WriteLine($"iCemDataMonitor.Host.exe: cli fatal: {ex.Message}"); }
            catch { /* stderr can be gone — the station log already has it */ }
            return ExitRpcFailed;
        }
    }

    /// <summary>Station log sink for cli mode (owner log policy; folder name
    /// kept for continuity — see the class header). Never throws.</summary>
    private static void Fatal(string line) => StationFileLog.Write(
        Path.Combine(StationFileLog.AppLogDir("iCemDataMonitor.Cli"), "cli.log"),
        line);

    internal static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<TimeSpan, Task<bool>>? pipeProbe = null,
        Func<IHostApi>? apiFactory = null,
        Func<string?>? hostExeResolver = null,
        Func<string, Process?>? hostSpawner = null,
        TimeSpan? startReadyTimeout = null,
        Func<string, bool>? eventSignaler = null)
    {
        pipeProbe ??= EnsureHost.PipeRespondsAsync;
        apiFactory ??= () => new GrpcHostClient();
        hostExeResolver ??= () => HostProcessLauncher.ResolveHostExePath(AppContext.BaseDirectory);
        hostSpawner ??= path => HostProcessLauncher.Spawn(path);
        // 2026-08-31 (слой CLI): lifecycle verbs poke through the SAME
        // production helper the HostLifecycle close path uses (one mechanism).
        eventSignaler ??= Tray.HostLifecycle.SignalNamedEvent;

        // ── argument parsing (fail-fast on anything unknown) ─────────────────
        bool startRequested = false;
        string? command = null;
        string? subcommand = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    Usage(stderr);
                    return ExitOk;
                case "--start":
                    startRequested = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        stderr.WriteLine($"iCemDataMonitor.Host.exe: unknown option '{args[i]}'");
                        Usage(stderr);
                        return ExitUsage;
                    }
                    if (command is null) command = args[i];
                    else if (subcommand is null) subcommand = args[i];
                    else
                    {
                        Usage(stderr);
                        return ExitUsage;
                    }
                    break;
            }
        }

        bool validShape = command switch
        {
            "snapshot" => subcommand is null,
            "header" => subcommand is null,
            "stop" => subcommand is null,
            "restart" => subcommand is null,
            "get" => subcommand is "main" or "lan" or "raw",
            _ => false,
        };
        if (!validShape)
        {
            if (command is not null)
                stderr.WriteLine($"iCemDataMonitor.Host.exe: unknown command '{command}{(subcommand is null ? "" : " " + subcommand)}'");
            Usage(stderr);
            return ExitUsage;
        }

        return RunAsync(command!, subcommand, startRequested, stdout, stderr,
            pipeProbe, apiFactory, hostExeResolver, hostSpawner, eventSignaler,
            startReadyTimeout ?? StartReadyTimeout).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        string command,
        string? subcommand,
        bool startRequested,
        TextWriter stdout,
        TextWriter stderr,
        Func<TimeSpan, Task<bool>> pipeProbe,
        Func<IHostApi> apiFactory,
        Func<string?> hostExeResolver,
        Func<string, Process?> hostSpawner,
        Func<string, bool> eventSignaler,
        TimeSpan startReadyTimeout)
    {
        // ── lifecycle verbs (2026-08-31, слой CLI «n+1 источник команд») ─────
        // The forwarder NEVER touches the process: it pokes the SAME
        // host-owned events the shell button uses; the host executes the verb
        // ITSELF through HostLifecycle (E.2.7). stdout stays PURE JSON.
        if (command == "stop")
        {
            var signalled = eventSignaler(Tray.CrossProcessNames.HostShutdownEvent);
            stdout.WriteLine(FormatAction("stop", signalled ? "signalled" : "notRunning"));
            if (!signalled)
                stderr.WriteLine("host not running — nothing to stop (stop is idempotent)");
            return ExitOk;
        }

        if (command == "restart")
        {
            if (eventSignaler(Tray.CrossProcessNames.HostRestartEvent))
            {
                // The blunt-exit + successor boot takes ~2-3 s: settle BEFORE
                // the first probe, otherwise the DYING old host's pipe can
                // answer and fake a READY.
                stderr.WriteLine(
                    $"iCemDataMonitor.Host.exe: restart signalled — waiting for the successor READY ≤{startReadyTimeout.TotalSeconds:0} s");
                await Task.Delay(RestartSettleDelay).ConfigureAwait(false);
                if (await WaitForReadyAsync(pipeProbe, startReadyTimeout).ConfigureAwait(false))
                {
                    stdout.WriteLine(FormatAction("restart", "restarted"));
                    return ExitOk;
                }
                stderr.WriteLine(
                    $"restart signalled, but the host did not become READY within {startReadyTimeout.TotalSeconds:0} s");
                return ExitUsage;
            }

            // The event does not exist: either the host is dead (⇒ restart =
            // start, systemctl semantics — the SAME spawn path as --start) or
            // it answers the pipe from an OLD build without the restart
            // event (honest diagnostics, never a silent spawn of a twin).
            if (await pipeProbe(ProbeTimeout).ConfigureAwait(false))
            {
                stderr.WriteLine(
                    "host answers the pipe but exposes NO restart event — старая сборка хоста; " +
                    "перезапустите его один раз из трея, дальше cli restart будет работать");
                return ExitRpcFailed;
            }

            var exeRestart = hostExeResolver();
            if (exeRestart is null)
            {
                stderr.WriteLine(
                    "host not running and iCemDataMonitor.Host.exe not found " +
                    "(sibling / host\\ / dev-walk-up resolver came up empty)");
                return ExitUsage;
            }
            hostSpawner(exeRestart);
            stderr.WriteLine(
                $"iCemDataMonitor.Host.exe: host spawned: {exeRestart} — waiting for READY ≤{startReadyTimeout.TotalSeconds:0} s");
            if (await WaitForReadyAsync(pipeProbe, startReadyTimeout).ConfigureAwait(false))
            {
                stdout.WriteLine(FormatAction("restart", "spawned"));
                return ExitOk;
            }
            stderr.WriteLine(
                $"host not running (pipe {HostChannelNames.PipeName} не отвечает) — " +
                $"spawned host did not become READY within {startReadyTimeout.TotalSeconds:0} s");
            return ExitUsage;
        }

        // ── ensure-host: the AUTHORITATIVE probe is the pipe (B.2.1 law) ────
        if (!await pipeProbe(ProbeTimeout).ConfigureAwait(false))
        {
            if (!startRequested)
            {
                stderr.WriteLine(
                    $"host not running (pipe {HostChannelNames.PipeName} не отвечает)");
                return ExitUsage;
            }

            var exe = hostExeResolver();
            if (exe is null)
            {
                stderr.WriteLine(
                    "host not running and iCemDataMonitor.Host.exe not found " +
                    "(sibling / host\\ / dev-walk-up resolver came up empty)");
                return ExitUsage;
            }
            hostSpawner(exe);
            stderr.WriteLine(
                $"iCemDataMonitor.Host.exe: host spawned: {exe} — waiting for READY ≤{startReadyTimeout.TotalSeconds:0} s");

            if (!await WaitForReadyAsync(pipeProbe, startReadyTimeout).ConfigureAwait(false))
            {
                stderr.WriteLine(
                    $"host not running (pipe {HostChannelNames.PipeName} не отвечает) — " +
                    $"spawned host did not become READY within {startReadyTimeout.TotalSeconds:0} s");
                return ExitUsage;
            }
        }
        using var api = apiFactory();
        try
        {
            switch (command)
            {
                case "header":
                {
                    var status = await api.GetStatusAsync().ConfigureAwait(false);
                    stdout.WriteLine(FormatHeader(status));
                    break;
                }
                case "get":
                {
                    var zone = subcommand switch
                    {
                        "main" => BusZoneKind.BusZoneCom,
                        "lan" => BusZoneKind.BusZoneLan,
                        _ => BusZoneKind.BusZoneRaw,
                    };
                    var reply = await api.GetRegistersAsync(zone).ConfigureAwait(false);
                    stdout.WriteLine(FormatZoneFloats(reply));
                    break;
                }
                case "snapshot":
                {
                    var reply = await api.GetRegistersAsync(BusZoneKind.BusZoneUnspecified)
                        .ConfigureAwait(false);
                    stdout.WriteLine(FormatSnapshotWords(reply));
                    break;
                }
            }
            return ExitOk;
        }
        catch (Exception ex)
        {
            // The host IS reachable (probe passed) — an RPC failure is honest
            // diagnostics (bus closed, UNAVAILABLE, …), never masked as
            // "host not running". W23: execution errors also land in the
            // station log (Logs\iCemDataMonitor.Cli\cli.log).
            Fatal($"rpc failed: {ex}");
            stderr.WriteLine($"iCemDataMonitor.Host.exe: rpc failed: {ex.Message}");
            return ExitRpcFailed;
        }
    }

    // ═══ stdout formatters — PURE functions, golden-tested ══════════════════

    /// <summary>2026-08-31 (слой CLI): settle delay AFTER a restart signal
    /// before the first READY probe — the old host blunt-exits in ~0.3-0.5 s
    /// and the successor boots in ~1.5-2 s; probing earlier can catch the
    /// DYING host's pipe and fake a READY.</summary>
    internal static readonly TimeSpan RestartSettleDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>Shared READY wait (the --start spawn path and the restart
    /// wait arm): probe once a second until the budget expires.</summary>
    private static async Task<bool> WaitForReadyAsync(
        Func<TimeSpan, Task<bool>> pipeProbe, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (await pipeProbe(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                return true;
            await Task.Delay(400).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>2026-08-31 (слой CLI): lifecycle-verb result — the stdout
    /// contract stays PURE JSON single-line for the machine verbs too:
    /// {"action":"stop|restart","result":"signalled|notRunning|restarted|spawned"}</summary>
    internal static string FormatAction(string action, string result) =>
        JsonSerializer.Serialize(new ActionOut(action, result), JsonOpts);

    internal readonly record struct ActionOut(string Action, string Result);

    /// <summary>Old header contract, byte-exact key set/order:
    /// {"comPortActive":N,"caseState":N,"caseLanState":N,"heartbeat":N}
    /// (comPortActive = ComPort > 0 ? 1 : 0 per the W20 F.2 directive).</summary>
    internal static string FormatHeader(HostStatus status)
    {
        var header = new HeaderOut(
            ComPortActive: status.ComPort > 0 ? 1 : 0,
            CaseState: status.CaseState,
            CaseLanState: status.CaseLanState,
            Heartbeat: status.Heartbeat);
        return JsonSerializer.Serialize(header, JsonOpts);
    }

    internal readonly record struct HeaderOut(
        int ComPortActive, int CaseState, int CaseLanState, int Heartbeat);

    /// <summary>OLD get format: JSON array of floats of the requested zone
    /// (main 98, lan 100, raw 100). The RPC projects pairs; the timestamp
    /// int32 pair at addr 4 carries RAW BITS in CDAB word order — re-interpreting
    /// those bits as float32 reproduces EXACTLY what the old direct CDAB read
    /// produced (a garbage-by-decree float, deterministic).</summary>
    internal static string FormatZoneFloats(RegistersReply reply)
    {
        var floats = new List<float>(reply.Values.Count);
        foreach (var v in reply.Values)
        {
            floats.Add(v.ValueCase == RegisterValue.ValueOneofCase.Int32Value
                ? ReinterpretBitsAsFloat(v.Int32Value)
                : Sanitize(v.FloatValue));
        }
        return JsonSerializer.Serialize(floats, JsonOpts);
    }

    /// <summary>OLD snapshot form: JSON array of u16 register words. Rebuilt
    /// from the UNSPECIFIED sweep: header words 0..3 surface as int32 (direct),
    /// every pair splits back into (low, high) CDAB words.</summary>
    internal static string FormatSnapshotWords(RegistersReply reply)
    {
        var words = new List<ushort>(reply.Values.Count * 2);
        foreach (var v in reply.Values)
        {
            switch (v.ValueCase)
            {
                case RegisterValue.ValueOneofCase.Int32Value:
                    uint bits = unchecked((uint)v.Int32Value);
                    words.Add((ushort)(bits & 0xFFFF));
                    if (v.Addr >= 4) // data-zone pair (timestamp): emit high word too
                        words.Add((ushort)(bits >> 16));
                    break;
                case RegisterValue.ValueOneofCase.FloatValue:
                    (ushort low, ushort high) = SplitCdab(Sanitize(v.FloatValue));
                    words.Add(low);
                    words.Add(high);
                    break;
            }
        }
        return JsonSerializer.Serialize(words, JsonOpts);
    }

    // ═══ helpers (local CDAB law mirror — the CLI mode may NOT reference Sdk's
    //     bus codecs; StationFileLog above is diagnostics-only) ═══════════════

    /// <summary>CdabCodec.Combine equivalent: raw pair bits → float32.</summary>
    private static float ReinterpretBitsAsFloat(int rawBits) =>
        BitConverter.UInt32BitsToSingle(unchecked((uint)rawBits));

    /// <summary>CdabCodec.Split equivalent: float32 → (low, high) CDAB words.</summary>
    private static (ushort Low, ushort High) SplitCdab(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        return ((ushort)(bits & 0xFFFF), (ushort)(bits >> 16));
    }

    /// <summary>JSON has no NaN/±∞ literals — non-finite floats become 0.0
    /// (same sanitize law as the old direct reader).</summary>
    private static float Sanitize(float f) => float.IsFinite(f) ? f : 0f;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 2026-08-31 (owner decree «пункт 3»): the usage banner moved here with
    /// the command logic; the exe name is now iCemDataMonitor.Host.exe (the
    /// icemdatamonitor-cli alias is retired — no stub exe). Exposed as text so
    /// HostArgs.HelpText can embed the SAME block (one source, no drift).
    /// </summary>
    internal static string UsageText =>
        "iCemDataMonitor.Host.exe cli mode — thin forwarder to the running host (pure-JSON stdout)" + Environment.NewLine +
        "every command is ONE gRPC call over the host named pipe — no local bus access" + Environment.NewLine +
        "usage:" + Environment.NewLine +
        "  iCemDataMonitor.Host.exe [--start] snapshot      JSON array of u16 registers (live window 0..599)" + Environment.NewLine +
        "  iCemDataMonitor.Host.exe [--start] get main|lan|raw" + Environment.NewLine +
        "                                                    main: com_stream 4..199 float32 CDAB (98)" + Environment.NewLine +
        "                                                    lan:  lan_stream_crio 200..399 float32 CDAB (100)" + Environment.NewLine +
        "                                                    raw:  raw_com_line 400..599 float32 CDAB positional (100)" + Environment.NewLine +
        "  iCemDataMonitor.Host.exe [--start] header        JSON header map:" + Environment.NewLine +
        "                                                    {comPortActive, caseState, caseLanState, heartbeat}" + Environment.NewLine +
        "lifecycle verbs (2026-08-31, слой CLI — same host-owned events as the tray/shell buttons):" + Environment.NewLine +
        "  iCemDataMonitor.Host.exe restart                  SET HostRestartEvent → host restarts ITSELF" + Environment.NewLine +
        "                                                    (tray [Перезапуск] path), wait READY ≤30 s;" + Environment.NewLine +
        "                                                    dead host ⇒ restart = start (systemctl semantics)" + Environment.NewLine +
        "                                                    stdout: {\"action\":\"restart\",\"result\":\"restarted|spawned\"}" + Environment.NewLine +
        "  iCemDataMonitor.Host.exe stop                     SET HostShutdownEvent → host closes ITSELF" + Environment.NewLine +
        "                                                    (tray [Закрыть] path); idempotent" + Environment.NewLine +
        "                                                    stdout: {\"action\":\"stop\",\"result\":\"signalled|notRunning\"}" + Environment.NewLine +
        "flags:" + Environment.NewLine +
        "  --start   spawn iCemDataMonitor.Host.exe (host mode) when the pipe does not answer," + Environment.NewLine +
        "            wait for READY ≤30 s, then execute the command (no local fallback)" + Environment.NewLine +
        "exit codes: 0 ok, 1 rpc failed (host answered), 2 usage / host not running";

    private static void Usage(TextWriter stderr) => stderr.WriteLine(UsageText);
}
