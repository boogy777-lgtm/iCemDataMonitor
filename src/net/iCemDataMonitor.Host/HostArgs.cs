namespace iCemDataMonitor.Host;

/// <summary>
/// D.3' command-line contract of the headless host. Fail-fast: ANY unknown
/// argument is an error (no silent ignore), reported in <see cref="Errors"/>.
/// </summary>
/// <param name="Port">--port N override (tests / port conflicts); null = ini web_port.</param>
/// <param name="IniPath">--ini PATH explicit config file; null = IniConfig canonical search order.</param>
/// <param name="Autostart">--autostart: launched by the HKCU Run-key (E.2.4) —
/// hide the console window. Manual runs omit it and keep the console visible.</param>
/// <param name="ShowHelp">--help requested.</param>
/// <param name="Errors">Parse violations (empty = OK).</param>
public sealed record HostArgs(int? Port, string? IniPath, bool Autostart, bool ShowHelp, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && !ShowHelp;

    public static HostArgs Parse(string[] args)
    {
        int? port = null;
        string? iniPath = null;
        bool autostart = false;
        bool help = false;
        var errors = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    help = true;
                    break;

                case "--autostart":
                    autostart = true;
                    break;

                case "--port":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var p) || p is < 0 or > 65535)
                        errors.Add("--port requires an integer in [0..65535] (0 = ephemeral, tests only)");
                    else
                        port = p;
                    break;

                case "--ini":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                        errors.Add("--ini requires a file path");
                    else
                        iniPath = args[i];
                    break;

                default:
                    errors.Add($"unknown argument '{args[i]}'");
                    break;
            }
        }

        return new HostArgs(port, iniPath, autostart, help, errors);
    }

    public static string HelpText =>
        """
        iCemDataMonitor.Host — headless station host (user console process, tray UI)

        Usage: iCemDataMonitor.Host [--port N] [--ini PATH] [--autostart]

          --port N      Web surface port override (default: config.ini web_port=8090).
                        0 binds an ephemeral port (tests only).
          --ini PATH    Explicit config file path (default: canonical search order —
                        %ProgramData%\Halliburton\iCem\DataMonitoring\config.ini,
                        legacy station.ini/config.ini candidates auto-migrated).
          --autostart   Launched by the HKCU Run-key: hide the console window
                        (manual runs keep it visible with a banner).
          --help        Show this text.

        CLI MODE (2026-08-31, owner decree «пункт 3» — the retired
        icemdatamonitor-cli.exe merged into this exe; NO stub alias remains):

        """ + CliMode.CliForwarder.UsageText;
}
