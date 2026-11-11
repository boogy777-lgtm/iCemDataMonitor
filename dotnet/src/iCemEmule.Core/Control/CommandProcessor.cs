// CommandProcessor — pure parser/dispatcher for the operator command channel.
// One input line → one ControlReply (JSON, single line). Two input grammars:
//   text:  status | start com | stop lan | set "Pump A Pressure" 5000 | ...
//   JSON:  {"cmd":"set","name":"Pump A Pressure","value":5000,"channel":"lan"}
// Malformed input ALWAYS yields {"ok":false,...} and never throws — the
// emitter loops must survive any garbage on the pipe (stand law).

using System.Globalization;
using System.Text.Json;

namespace Halliburton.iCemEmule.Control;

public static class CommandProcessor
{
    /// <summary>W11.10 (audit A4): pipe protocol version — reported in every
    /// status reply; UI warns on mismatch (engine/UI deployed separately).</summary>
    public const int ProtoVersion = 1;

    private static readonly string[] CommandNames =
    {
        "status", "start", "stop", "set", "get", "clear",
        "profiles", "profile", "vars", "watch", "quit", "help",
        // W11.7: full embedded-UI surface
        "ports", "adapters", "setport", "setadapter", "setequipment",
        "setfirmware", "setemulate", "setpreset", "setunit"
    };

    public static ControlReply ProcessLine(string? line, IEmuControl ctl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return Error("", "empty command line");

            string? cmd, channel, name, deviceType, svText;
            double? value;

            if (line.TrimStart().StartsWith('{'))
            {
                if (!TryParseJson(line, out cmd, out channel, out name, out value, out deviceType, out svText, out string jsonErr))
                    return Error("", jsonErr);
            }
            else
            {
                var tokens = Tokenize(line);
                if (tokens.Count == 0)
                    return Error("", "empty command line");
                cmd = tokens[0];
                channel = null; name = null; value = null; deviceType = null; svText = null;

                switch (cmd.ToLowerInvariant())
                {
                    case "start" or "stop":
                        if (tokens.Count < 2) return Error(cmd, $"'{cmd}' requires a channel (com|lan)");
                        channel = tokens[1];
                        break;
                    case "set":
                        if (tokens.Count < 3) return Error(cmd, "'set' requires a name and a value: set \"<name>\" <value>");
                        if (!TryParseNumber(tokens[^1], out double v))
                            return Error(cmd, $"value '{tokens[^1]}' is not a number");
                        value = v;
                        name = string.Join(' ', tokens.Skip(1).Take(tokens.Count - 2));
                        break;
                    case "get" or "clear":
                        if (tokens.Count < 2) return Error(cmd, $"'{cmd}' requires a variable name");
                        name = string.Join(' ', tokens.Skip(1));
                        break;
                    case "profile":
                        if (tokens.Count < 2) return Error(cmd, "'profile' requires a template name (see 'profiles')");
                        name = string.Join(' ', tokens.Skip(1));
                        break;
                    case "vars":
                        if (tokens.Count >= 2) channel = tokens[1];
                        break;
                    case "setport":
                        if (tokens.Count < 2) return Error(cmd, "'setport' requires a port name");
                        name = string.Join(' ', tokens.Skip(1));
                        break;
                    case "setadapter":
                        if (tokens.Count < 2) return Error(cmd, "'setadapter' requires an adapter name");
                        name = string.Join(' ', tokens.Skip(1));
                        break;
                    case "setequipment":
                        if (tokens.Count < 2) return Error(cmd, "'setequipment' requires 0x... or decimal");
                        if (!TryParseHexOrDec(tokens[1], out double eq)) return Error(cmd, $"equipment '{tokens[1]}' is not a number");
                        value = eq;
                        break;
                    case "setfirmware":
                        if (tokens.Count < 2 || !TryParseNumber(tokens[1], out double fw)) return Error(cmd, "'setfirmware' requires 0|1");
                        value = fw;
                        break;
                    case "setemulate":
                        if (tokens.Count < 3 || !TryParseNumber(tokens[2], out double em)) return Error(cmd, "'setemulate' requires a channel and 0|1: setemulate com 0");
                        channel = tokens[1];
                        value = em;
                        break;
                    case "setpreset":
                        if (tokens.Count < 2 || !TryParseNumber(tokens[1], out double pr)) return Error(cmd, "'setpreset' requires 0..3");
                        value = pr;
                        break;
                    case "setunit":
                        if (tokens.Count < 2) return Error(cmd, "'setunit' requires a group (flow|temperature|density|volume|level|pressure) and optional unit");
                        channel = tokens[1];                 // group rides in `channel`
                        name = string.Join(' ', tokens.Skip(2)); // "" = clear override
                        break;
                }
            }

            cmd = (cmd ?? string.Empty).Trim().ToLowerInvariant();
            (channel, name) = SplitChannelPrefix(channel, name);
            uint? sv = ParseSv(svText, out string svErr);
            if (svText is not null && sv is null)
                return Error(cmd, svErr);

            switch (cmd)
            {
                case "status":
                    {
                        var status = ctl.GetStatus();
                        status["proto"] = ProtoVersion;
                        return Ok(cmd, status);
                    }
                case "start":
                    if (!ValidChannel(channel, out string cerr))
                        return Error(cmd, cerr);
                    return From(cmd, ctl.StartChannel(channel!));
                case "stop":
                    if (!ValidChannel(channel, out cerr))
                        return Error(cmd, cerr);
                    return From(cmd, ctl.StopChannel(channel!));
                case "set":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'set' requires a variable name");
                    if (value is null)
                        return Error(cmd, "'set' requires a numeric value");
                    return From(cmd, ctl.SetVariable(channel, name, value.Value, sv));
                case "get":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'get' requires a variable name");
                    return From(cmd, ctl.GetVariable(channel, name, sv));
                case "clear":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'clear' requires a variable name");
                    return From(cmd, ctl.ClearVariable(channel, name, sv));
                case "profiles":
                    return From(cmd, ctl.GetProfiles());
                case "profile":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'profile' requires a template name (see 'profiles')");
                    return From(cmd, ctl.SetProfile(deviceType, name));
                case "vars":
                    if (channel is not null && !ValidChannel(channel, out cerr))
                        return Error(cmd, cerr);
                    return From(cmd, ctl.GetVariables(channel));
                case "watch":
                    return new ControlReply(cmd, ToJson(true, cmd, new Dictionary<string, object?> { ["intervalSec"] = 1.0 }, null), StartWatch: true, Quit: false);
                case "quit":
                    ctl.RequestQuit();
                    return new ControlReply(cmd, ToJson(true, cmd, new Dictionary<string, object?> { ["quitting"] = true }, null), StartWatch: false, Quit: true);
                case "help":
                    return Ok(cmd, new Dictionary<string, object?> { ["commands"] = CommandNames });
                case "ports":
                    return From(cmd, ctl.GetPorts());
                case "adapters":
                    return From(cmd, ctl.GetAdapters());
                case "setport":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'setport' requires a port name");
                    return From(cmd, ctl.SetPort(name));
                case "setadapter":
                    if (string.IsNullOrEmpty(name))
                        return Error(cmd, "'setadapter' requires an adapter name");
                    return From(cmd, ctl.SetAdapter(name));
                case "setequipment":
                    if (value is null || value.Value < 0 || value.Value > uint.MaxValue)
                        return Error(cmd, "'setequipment' requires 0x... or decimal uint32");
                    return From(cmd, ctl.SetEquipment((uint)value.Value));
                case "setfirmware":
                    if (value is null)
                        return Error(cmd, "'setfirmware' requires 0|1");
                    return From(cmd, ctl.SetFirmware(value.Value != 0));
                case "setemulate":
                    if (!ValidChannel(channel, out cerr))
                        return Error(cmd, cerr);
                    if (value is null)
                        return Error(cmd, "'setemulate' requires 0|1");
                    return From(cmd, ctl.SetEmulation(channel!, value.Value != 0));
                case "setpreset":
                    if (value is null || value.Value < 0 || value.Value > 3)
                        return Error(cmd, "'setpreset' requires 0..3");
                    return From(cmd, ctl.SetPreset((int)value.Value));
                case "setunit":
                    if (string.IsNullOrEmpty(channel))
                        return Error(cmd, "'setunit' requires a group");
                    return From(cmd, ctl.SetUnit(channel, string.IsNullOrEmpty(name) ? null : name));
                default:
                    return Error(cmd, "unknown command '" + cmd + "'; expected one of: " + string.Join(", ", CommandNames));
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: a control-plane failure must NEVER take down the emitters.
            return Error("", $"internal error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── reply builders ──────────────────────────────────────────────────
    private static ControlReply Ok(string cmd, Dictionary<string, object?> data)
        => new(cmd, ToJson(true, cmd, data, null), false, false);

    private static ControlReply Error(string cmd, string error)
        => new(cmd, ToJson(false, cmd, null, error), false, false);

    private static ControlReply From(string cmd, ControlResult r)
        => new(cmd, ToJson(r.Ok, cmd, r.Data, r.Error), false, false);

    public static string ToJson(bool ok, string cmd, Dictionary<string, object?>? data, string? error)
    {
        var d = new Dictionary<string, object?> { ["ok"] = ok, ["cmd"] = cmd };
        if (ok)
        {
            if (data is not null) d["data"] = data;
        }
        else
        {
            d["error"] = error ?? "error";
        }
        return JsonSerializer.Serialize(d);
    }

    // ── parsing helpers ─────────────────────────────────────────────────
    private static bool ValidChannel(string? channel, out string error)
    {
        error = string.Empty;
        if (channel is null) { error = "channel required (com|lan)"; return false; }
        if (channel.Equals("com", StringComparison.OrdinalIgnoreCase) ||
            channel.Equals("lan", StringComparison.OrdinalIgnoreCase))
            return true;
        error = $"unknown channel '{channel}' (com|lan)";
        return false;
    }

    /// <summary>name prefix "com.X" / "lan.X" forces the channel.</summary>
    private static (string? Channel, string? Name) SplitChannelPrefix(string? channel, string? name)
    {
        if (name is null) return (channel, name);
        foreach (var prefix in new[] { "com.", "lan." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (prefix[..^1], name[prefix.Length..]);
        }
        return (channel, name);
    }

    private static uint? ParseSv(string? svText, out string error)
    {
        error = string.Empty;
        if (svText is null) return null;
        var t = svText.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return hex;
        if (uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
            return dec;
        error = $"sv '{svText}' is not a uint32 (decimal or 0x...)";
        return null;
    }

    internal static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Hex (0x...) or decimal → double (exact for uint32).</summary>
    private static bool TryParseHexOrDec(string text, out double value)
    {
        value = 0;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
        {
            value = hex;
            return true;
        }
        return double.TryParse(t, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Whitespace tokenizer with double-quote grouping.</summary>
    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, hasToken = false;
        foreach (char c in line)
        {
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                else cur.Append(c);
            }
            else if (c == '"') { inQuotes = true; hasToken = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0 || hasToken) { tokens.Add(cur.ToString()); cur.Clear(); hasToken = false; }
            }
            else { cur.Append(c); hasToken = true; }
        }
        if (cur.Length > 0 || hasToken) tokens.Add(cur.ToString());
        return tokens;
    }

    private static bool TryParseJson(string line,
        out string? cmd, out string? channel, out string? name, out double? value,
        out string? deviceType, out string? sv, out string error)
    {
        cmd = channel = name = deviceType = sv = null;
        value = null;
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON command must be an object with a 'cmd' string property";
                return false;
            }
            if (!root.TryGetProperty("cmd", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
            {
                error = "missing 'cmd' string property";
                return false;
            }
            cmd = cmdEl.GetString();
            channel = GetStr(root, "channel");
            name = GetStr(root, "name");
            deviceType = GetStr(root, "deviceType");
            sv = GetStr(root, "sv");
            if (root.TryGetProperty("value", out var vEl))
            {
                if (vEl.ValueKind == JsonValueKind.Number && vEl.TryGetDouble(out double d))
                    value = d;
                else if (vEl.ValueKind == JsonValueKind.String && TryParseNumber(vEl.GetString()!, out d))
                    value = d;
                else
                {
                    error = "'value' must be a number";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? GetStr(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
