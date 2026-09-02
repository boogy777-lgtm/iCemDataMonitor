// ControlClient — the CLI mode of the merged emulator app
// (`iCemEmulator.exe --cli ...`, 2026-08-27 owner decree; exe renamed from
// iCemEmule.exe --ctl): connects to the RUNNING instance over
// \\.\pipe\iCemEmule.Control, sends one command, prints the JSON reply
// line(s) to stdout, and exits (0 = ok, 1 = command/server error, 2 = usage).
// With no command arguments and redirected stdin it acts as a REPL: one
// command per stdin line, one JSON reply per stdout line. When NO instance
// runs it honestly errors «эмулятор не запущен» (mirroring the host CLI
// semantics). WinExe has no console: attach to the parent console when stdout
// is not already redirected so interactive use prints too.
//
// P2 2026-08-26 (owner decree «UI должен использовать CLI-команды как
// стандарт в нашей экосистеме»): the pipe connection + reply parsing live in
// the SHARED ControlPipeClient — the CLI and the WinUI use the SAME client
// library now. The CLI keeps its own connect-error granularity (unreachable
// pipe vs connection-closed) and the watch-stream loop — observable CLI
// behavior is byte-identical (pinned by contract).

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Halliburton.iCemEmule.Control;

public static class ControlClient
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static int Run(string[] args)
    {
        TextWriter output = OpenOutput();
        try
        {
            if (args.Length == 0)
            {
                if (!Console.IsInputRedirected)
                {
                    // P1 2026-08-26 (CLI/UI parity decree): the usage line must
                    // list the SAME control surface the embedded UI uses —
                    // keep it in sync with CommandProcessor.CommandNames.
                    output.WriteLine("{\"ok\":false,\"cmd\":\"help\",\"error\":\"usage: iCemEmulator.exe --cli <command> " +
                        "(status|start|stop|set|get|clear|profiles|profile|vars|ports|adapters|setport|setadapter|" +
                        "setequipment|setfirmware|setemulate|setpreset|setunit|watch|quit|help), or pipe commands via stdin\"}");
                    return 2;
                }
                int worst = 0;
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                {
                    int code = RoundTrip(line, output);
                    if (code != 0) worst = code;
                    if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                }
                return worst;
            }

            return RoundTrip(string.Join(' ', args), output);
        }
        finally
        {
            output.Flush();
        }
    }

    private static int RoundTrip(string command, TextWriter output)
    {
        try
        {
            using var pipe = ControlPipeClient.Connect(2000);
            if (pipe is null)
            {
                // 2026-08-27 owner decree: honest «эмулятор не запущен» when
                // no instance runs (mirror the host CLI semantics).
                output.WriteLine("{\"ok\":false,\"cmd\":\"\",\"error\":\"эмулятор не запущен " +
                    "(pipe " + ControlPipeServer.DefaultPipeName +
                    " недоступна — запустите iCemEmulator.exe)\"}");
                return 1;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            bool watch = CommandProcessor.Tokenize(command).FirstOrDefault()
                ?.Equals("watch", StringComparison.OrdinalIgnoreCase) == true;

            writer.WriteLine(command);
            string? reply = reader.ReadLine();
            if (reply is null)
            {
                output.WriteLine("{\"ok\":false,\"cmd\":\"\",\"error\":\"connection closed by server\"}");
                return 1;
            }
            output.WriteLine(reply);
            if (!IsOk(reply)) return 1;

            if (watch)
            {
                string? tick;
                while ((tick = reader.ReadLine()) is not null)
                    output.WriteLine(tick);
            }
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"{{\"ok\":false,\"cmd\":\"\",\"error\":\"client error: {ex.Message}\"}}");
            return 1;
        }
    }

    private static bool IsOk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    private static TextWriter OpenOutput()
    {
        try
        {
            if (!Console.IsOutputRedirected)
                AttachConsole(AttachParentProcess); // best effort: print to parent console
            return new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        }
        catch
        {
            return TextWriter.Null;
        }
    }
}
