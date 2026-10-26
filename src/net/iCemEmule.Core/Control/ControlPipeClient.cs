// ControlPipeClient — the ONE library-level client of the operator command
// channel (P2 2026-08-26, owner decree «UI должен использовать CLI-команды
// как стандарт в нашей экосистеме»): the CLI (ControlClient) and the WinUI
// (EmulatorViewModel, formerly its private EmuPipeClient) both talk to the
// headless engine through THIS class — same pipe (ControlPipeServer.
// DefaultPipeName), same wire format (JSON lines, proto v1), same parsing.
// NEVER throws: engine down / pipe busy / malformed reply degrade to null so
// the UI falls back to the stub (existing degradation contract).
//
// P5 2026-08-28 (root cause of the owner's «НЕ РАБОТАЮТ ПЕРЕКЛЮЧАТЕЛИ»):
// the OS pipe is an AMBIGUOUS channel when a second engine serves the same
// name. Live repro on the stand: legacy iCemEmule.exe (pid A) + merged
// iCemEmulator.exe (pid B) both bound \\.\pipe\iCemEmule.Control and 8 CLI
// `status` calls split 4/4 between the two pids — a UI toggle was answered
// by the WRONG engine while the 1 Hz snapshot read the in-proc one. The
// merged app therefore routes UI commands through LocalExecutor (set by its
// composition root): the SAME JSON grammar executed straight against the
// IN-PROC engine — one source of truth, no OS-pipe hop. The pipe server
// stays bound for the CLI and external tools (their target is "the running
// instance" — with a legacy engine also up, the CLI stays ambiguous by
// design; the legacy app is scheduled for retirement).

using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Halliburton.iCemEmule.Control;

public static class ControlPipeClient
{
    /// <summary>
    /// P5 2026-08-28: IN-PROC fast path for the merged app (composition-root
    /// wiring only — App.OnLaunched sets it to engine.Execute; nothing else
    /// may assign it). When set, EVERY SendCommand executes the same wire
    /// line against this executor instead of the OS pipe — the reply JSON is
    /// parsed by the same ParseReply, so the envelope contract (and every
    /// consumer of this class, VM included) is unchanged. Takes precedence
    /// over the pipeName argument: the in-proc engine IS the target. An
    /// executor exception degrades to null (never throws) — same degradation
    /// law as the pipe path. The legacy TrayApp never sets it ⇒ pipe path.
    /// </summary>
    public static Func<string, ControlReply?>? LocalExecutor { get; set; }

    /// <summary>Parsed reply envelope of one command round trip.</summary>
    public sealed record Reply(bool Ok, string Cmd, string? Error, JsonDocument Raw) : IDisposable
    {
        public void Dispose() => Raw.Dispose();
    }

    /// <summary>
    /// Open a connected pipe to the running engine. Returns null when the
    /// engine is down / the pipe is unreachable. The caller owns the stream.
    /// <paramref name="pipeName"/> defaults to the canonical operator channel;
    /// tests pass a unique name so the live engine on the stand is untouched.
    /// </summary>
    public static NamedPipeClientStream? Connect(int timeoutMs = 2000, string? pipeName = null)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName ?? ControlPipeServer.DefaultPipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(timeoutMs);
            }
            catch
            {
                pipe.Dispose();
                return null;
            }
            pipe.ReadMode = PipeTransmissionMode.Byte;
            return pipe;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse one JSON reply line into a Reply. Malformed JSON / missing shape
    /// ⇒ null (never throws).
    /// </summary>
    public static Reply? ParseReply(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            return new Reply(ok, cmd, error, doc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One command round trip (one line in, one line out). Null = engine down
    /// or reply unreadable. Caller disposes the returned Reply.
    /// </summary>
    public static Reply? SendCommand(string command, string? pipeName = null, int timeoutMs = 3000)
    {
        try
        {
            // P5: in-proc fast path — same wire line, same reply parsing, no
            // OS-pipe hop (see LocalExecutor doc; kills the two-server
            // cross-talk the toggles died of).
            var local = LocalExecutor;
            if (local is not null)
            {
                var executed = local(command);
                return executed is null ? null : ParseReply(executed.Json);
            }

            using var pipe = Connect(timeoutMs, pipeName);
            if (pipe is null)
                return null;

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(command);

            using var cts = new CancellationTokenSource(timeoutMs);
            string? line;
            try
            {
                line = reader.ReadLineAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return null; // timeout / server closed without a reply
            }
            return string.IsNullOrWhiteSpace(line) ? null : ParseReply(line);
        }
        catch
        {
            return null;
        }
    }

    // ── typed JSON helpers (the same JSON grammar the CLI's text forms map to) ──

    public static Reply? Cmd(string cmd)
        => SendCommand(JsonSerializer.Serialize(new Dictionary<string, object?> { ["cmd"] = cmd }));

    public static Reply? Cmd(string cmd, string argName, string argValue)
        => SendCommand(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = cmd,
            [argName] = argValue,
        }));

    /// <summary>Typed multi-arg form — values serialize with their CLR type
    /// (string/double/bool), so `set` can carry a numeric value and `sv` a hex string.</summary>
    public static Reply? Cmd(string cmd, IReadOnlyDictionary<string, object?> args)
    {
        var dict = new Dictionary<string, object?> { ["cmd"] = cmd };
        foreach (var kv in args)
            dict[kv.Key] = kv.Value;
        return SendCommand(JsonSerializer.Serialize(dict));
    }
}
