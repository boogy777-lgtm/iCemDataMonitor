// EmulatorAppContract + EmulatorAppArgs — the merged-app identity and command
// line dispatch (owner decree 2026-08-27: «эмулятор = отдельное независимое
// приложение со встроенным UI + CLI, своя папка»).
//
// Why in iCemEmule.Core (not the app assembly): the app csproj is a WinUI 3
// exe — the net10.0-windows test assembly (iCemEmule.Tests) cannot reference
// it. The contract lives here so the pins (exe name, process name, mutex,
// CLI flag, deploy folder — the transport live gate's cross-zone pins) are
// executable tests, not comments.
//
// SCHEMA DELTA (owner decree, 2026-08-27):
//   iCemEmule.exe --ctl ...  →  iCemEmulator.exe --cli ...   (same pipe
//   grammar/exit codes; the wire command table is UNCHANGED, only the exe
//   name and the CLI flag are renamed).

namespace Halliburton.iCemEmule.Control;

/// <summary>Cross-zone identity pins of the merged emulator app.</summary>
public static class EmulatorAppContract
{
    /// <summary>Single-instance mutex (UI mode only; --cli must NOT take it).</summary>
    public const string SingleInstanceMutexName = @"Global\iCemEmulator_v1";

    /// <summary>Process name (transport live gate pin: GetProcessesByName).</summary>
    public const string ProcessName = "iCemEmulator";

    /// <summary>Exe file name.</summary>
    public const string ExeFileName = "iCemEmulator.exe";

    /// <summary>Deployment folder name under the monitor root.</summary>
    public const string DeployFolderName = "iCemEmulator";

    /// <summary>Deployment root (installer does placement; projects/layout
    /// expectations are set here and in specs/iCemEmule).</summary>
    public const string MonitorRoot = @"C:\Program Files\Halliburton\iCemMonitor";

    /// <summary>Final deployment folder of the app.</summary>
    public static string DeployFolderPath =>
        System.IO.Path.Combine(MonitorRoot, DeployFolderName);

    /// <summary>CLI mode flag (the old --ctl died with iCemEmule.exe).</summary>
    public const string CliFlag = "--cli";
}

/// <summary>Merged-app command-line mode.</summary>
public enum EmulatorAppMode
{
    /// <summary>Default: no args (or unknown args) ⇒ the WinUI window.
    /// Unknown flags never crash the app — the UI always opens (default-UI law).</summary>
    Ui,

    /// <summary>One-shot console command against the RUNNING instance over the
    /// control pipe; errors «эмулятор не запущен» when no instance runs.</summary>
    Cli,

    /// <summary>--selftest: FrameRoundTrip.RunAll(), exit code = fail count.</summary>
    SelfTest,

    /// <summary>--landiag &lt;adapter&gt;: LAN loopback diagnostic.</summary>
    LanDiag,

    /// <summary>--help: usage text.</summary>
    Help,
}

/// <summary>Pure mode parser (unit-tested in iCemEmule.Tests).</summary>
public static class EmulatorAppArgs
{
    /// <summary>
    /// Parse the merged app's command line. First token decides the mode;
    /// the rest travels with the mode (CLI/LanDiag). DEFAULT-UI LAW: unknown
    /// or empty argv ⇒ Ui mode, no failure.
    /// </summary>
    public static EmulatorAppMode Parse(string[] args, out string[] rest)
    {
        rest = Array.Empty<string>();
        if (args is null || args.Length == 0)
            return EmulatorAppMode.Ui;

        switch (args[0].Trim().ToLowerInvariant())
        {
            case EmulatorAppContract.CliFlag:
                rest = args.Skip(1).ToArray();
                return EmulatorAppMode.Cli;
            case "--selftest":
                return EmulatorAppMode.SelfTest;
            case "--landiag":
                rest = args.Skip(1).ToArray();
                return EmulatorAppMode.LanDiag;
            case "--help":
            case "-h":
            case "/?":
                return EmulatorAppMode.Help;
            default:
                rest = args;
                return EmulatorAppMode.Ui;
        }
    }

    /// <summary>Usage banner for --help / bad --landiag (mirrors the old
    /// iCemEmule banner; the CLI grammar itself is unchanged).</summary>
    public static string Usage =>
        "iCemEmulator — стендовый эмулятор iCem (COM FLECS + LAN ACE), приложение со встроенным UI и CLI.\r\n" +
        "  (no args)               запуск приложения (окно WinUI; движок работает в процессе)\r\n" +
        $"  {EmulatorAppContract.CliFlag} <command>      однократная команда работающему экземпляру через \\\\.\\pipe\\iCemEmule.Control\r\n" +
        "                          (status|start <com|lan>|stop <com|lan>|set <var> <val>|get|clear|profiles|\r\n" +
        "                           profile|vars|ports|adapters|setport|setadapter|setequipment|setfirmware|\r\n" +
        "                           setemulate|setpreset|setunit|watch|quit|help); exit 0 ok / 1 error / 2 usage\r\n" +
        "  --selftest              FrameRoundTrip самотесты проводного формата\r\n" +
        "  --landiag <adapter>     LAN loopback диагностика\r\n" +
        "  --help                  эта справка";
}
