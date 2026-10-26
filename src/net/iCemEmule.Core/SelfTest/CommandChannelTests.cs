// CommandChannelTests — self-tests T21..T26 for the operator command channel
// (Control/CommandProcessor + Control/ControlPipeServer).
// T21: text/JSON parse incl. quoted names; malformed input → JSON error, no throw.
// T22: error handling — unknown cmd, bad channel, unknown variable, bad sv.
// T23: set/get/clear roundtrip (fake control + REAL SimulationEngine override).
// T24: start/stop idempotence per channel.
// T25: status reply JSON shape (parses, required keys present).
// T26: named-pipe end-to-end — status roundtrip, malformed line, watch tick, quit.

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;

namespace Halliburton.iCemEmule.SelfTest
{
    public static class CommandChannelTests
    {
        public static int RunAll()
        {
            int failCount = 0;
            failCount += Test21_Parse();
            failCount += Test22_ErrorHandling();
            failCount += Test23_SetGetRoundtrip();
            failCount += Test24_StartStopIdempotence();
            failCount += Test25_StatusJsonShape();
            failCount += Test26_PipeEndToEnd();
            return failCount;
        }

        // ── T21 ─────────────────────────────────────────────────────────
        private static int Test21_Parse()
        {
            int fails = 0;
            var ctl = new FakeEmuControl();

            // text forms
            if (!ReplyOk(CommandProcessor.ProcessLine("status", ctl)))
            { Console.WriteLine("T21 FAIL: 'status' text form not ok"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("  status  ", ctl)))
            { Console.WriteLine("T21 FAIL: 'status' with whitespace not ok"); fails++; }

            // JSON form
            if (!ReplyOk(CommandProcessor.ProcessLine("{\"cmd\":\"status\"}", ctl)))
            { Console.WriteLine("T21 FAIL: JSON status form not ok"); fails++; }

            // quoted name with spaces
            var setReply = CommandProcessor.ProcessLine("set \"Pump A Pressure\" 5000", ctl);
            if (!ReplyOk(setReply) || !ctl.Vars.TryGetValue("Pump A Pressure", out double v) || Math.Abs(v - 5000) > 1e-9)
            { Console.WriteLine($"T21 FAIL: quoted set did not apply: {setReply.Json}"); fails++; }

            // tokenization: name assembled from multiple bare tokens
            var setReply2 = CommandProcessor.ProcessLine("set Pump A Pressure 123.5", ctl);
            if (!ReplyOk(setReply2) || Math.Abs(ctl.Vars["Pump A Pressure"] - 123.5) > 1e-9)
            { Console.WriteLine($"T21 FAIL: unquoted multi-token set did not apply: {setReply2.Json}"); fails++; }

            // malformed inputs → JSON error, NEVER throw
            string[] garbage = { "", "   ", "{not json", "[]", "{\"value\":1}", "set", "set foo", "set foo bar" };
            foreach (var g in garbage)
            {
                try
                {
                    var r = CommandProcessor.ProcessLine(g, ctl);
                    if (ReplyOk(r))
                    { Console.WriteLine($"T21 FAIL: malformed '{g}' returned ok: {r.Json}"); fails++; }
                }
                catch (Exception ex)
                { Console.WriteLine($"T21 FAIL: malformed '{g}' threw {ex.GetType().Name}"); fails++; }
            }

            // channel prefix: set lan.X routes to lan channel
            var p = CommandProcessor.ProcessLine("set lan.Pump A Pressure 42", ctl);
            if (!ReplyOk(p) || ctl.LastChannel != "lan")
            { Console.WriteLine($"T21 FAIL: lan. prefix not honored: {p.Json}"); fails++; }

            if (fails == 0)
                Console.WriteLine("T21 PASS: parse (text/JSON/quotes/prefix) + malformed → JSON error, no throw");
            return fails;
        }

        // ── T22 ─────────────────────────────────────────────────────────
        private static int Test22_ErrorHandling()
        {
            int fails = 0;
            var ctl = new FakeEmuControl();

            void ExpectError(string line, string because)
            {
                var r = CommandProcessor.ProcessLine(line, ctl);
                if (ReplyOk(r) || !r.Json.Contains("\"error\""))
                { Console.WriteLine($"T22 FAIL: '{line}' expected error ({because}), got {r.Json}"); fails++; }
            }

            ExpectError("frobnicate", "unknown command");
            ExpectError("start", "missing channel");
            ExpectError("start foo", "bad channel");
            ExpectError("stop udp", "bad channel");
            ExpectError("get No Such Variable", "unknown variable");
            ExpectError("set No Such Variable 1", "unknown variable");
            ExpectError("profile No Such Template", "unknown profile");
            ExpectError("{\"cmd\":\"set\",\"name\":\"Pump A Pressure\",\"value\":\"abc\"}", "non-numeric value");
            ExpectError("{\"cmd\":\"set\",\"name\":\"Pump A Pressure\",\"value\":1,\"sv\":\"zz\"}", "bad sv");

            if (fails == 0)
                Console.WriteLine("T22 PASS: error handling (unknown cmd/channel/variable, bad value/sv)");
            return fails;
        }

        // ── T23 ─────────────────────────────────────────────────────────
        private static int Test23_SetGetRoundtrip()
        {
            int fails = 0;
            var ctl = new FakeEmuControl();

            // fake roundtrip: set → get → clear → get(base)
            if (!ReplyOk(CommandProcessor.ProcessLine("set \"Pump A Pressure\" 5000", ctl)))
            { Console.WriteLine("T23 FAIL: set failed"); fails++; }
            var g = CommandProcessor.ProcessLine("get \"Pump A Pressure\"", ctl);
            if (!ReplyOk(g) || !g.Json.Contains("5000"))
            { Console.WriteLine($"T23 FAIL: get after set: {g.Json}"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("clear \"Pump A Pressure\"", ctl)))
            { Console.WriteLine("T23 FAIL: clear failed"); fails++; }
            var g2 = CommandProcessor.ProcessLine("get \"Pump A Pressure\"", ctl);
            if (!ReplyOk(g2) || g2.Json.Contains("5000"))
            { Console.WriteLine($"T23 FAIL: get after clear must be base value: {g2.Json}"); fails++; }

            // REAL SimulationEngine: override lands in Tick, clear reverts
            var engine = new SimulationEngine();
            var tmpl = engine.Current;
            if (tmpl is null || tmpl.Variables.Length == 0)
            { Console.WriteLine("T23 FAIL: no templates loaded for engine check"); return fails + 1; }
            var target = tmpl.Variables.First(v => !string.Equals(v.Format, "elapsed", StringComparison.OrdinalIgnoreCase)
                                                && v.TogglePeriod <= 0);
            engine.SetOverride(target.Name, 777.25);
            var vals = engine.Tick();
            int idx = Array.IndexOf(tmpl.Variables, target);
            if (Math.Abs(vals[idx] - 777.25) > 1e-9)
            { Console.WriteLine($"T23 FAIL: engine Tick override: {vals[idx]} != 777.25"); fails++; }
            engine.ClearOverride(target.Name);
            var vals2 = engine.Tick();
            double expectedBase = SimulationEngine.ComputeValue(target, 2.0);
            if (Math.Abs(vals2[idx] - expectedBase) > 1e-9)
            { Console.WriteLine($"T23 FAIL: engine Tick after clear: {vals2[idx]} != {expectedBase}"); fails++; }
            if (engine.LastValues.Length != vals2.Length)
            { Console.WriteLine("T23 FAIL: LastValues not populated"); fails++; }

            if (fails == 0)
                Console.WriteLine("T23 PASS: set/get/clear roundtrip (fake + real SimulationEngine override/revert)");
            return fails;
        }

        // ── T24 ─────────────────────────────────────────────────────────
        private static int Test24_StartStopIdempotence()
        {
            int fails = 0;
            var ctl = new FakeEmuControl();

            if (!ReplyOk(CommandProcessor.ProcessLine("start com", ctl))) { Console.WriteLine("T24 FAIL: start com #1"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("start com", ctl))) { Console.WriteLine("T24 FAIL: start com #2 (idempotent)"); fails++; }
            if (ctl.ComStarted != 1) { Console.WriteLine($"T24 FAIL: com started {ctl.ComStarted}×, expected 1"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("stop com", ctl))) { Console.WriteLine("T24 FAIL: stop com #1"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("stop com", ctl))) { Console.WriteLine("T24 FAIL: stop com #2 (idempotent)"); fails++; }
            if (ctl.ComStopped != 1) { Console.WriteLine($"T24 FAIL: com stopped {ctl.ComStopped}×, expected 1"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("start lan", ctl))) { Console.WriteLine("T24 FAIL: start lan"); fails++; }
            if (!ReplyOk(CommandProcessor.ProcessLine("stop lan", ctl))) { Console.WriteLine("T24 FAIL: stop lan"); fails++; }
            if (ctl.LanStarted != 1 || ctl.LanStopped != 1)
            { Console.WriteLine($"T24 FAIL: lan started {ctl.LanStarted}×/stopped {ctl.LanStopped}×, expected 1/1"); fails++; }

            if (fails == 0)
                Console.WriteLine("T24 PASS: start/stop idempotence per channel (com + lan)");
            return fails;
        }

        // ── T25 ─────────────────────────────────────────────────────────
        private static int Test25_StatusJsonShape()
        {
            int fails = 0;
            var ctl = new FakeEmuControl();
            var r = CommandProcessor.ProcessLine("status", ctl);
            try
            {
                using var doc = JsonDocument.Parse(r.Json);
                var root = doc.RootElement;
                if (!root.GetProperty("ok").GetBoolean()) { Console.WriteLine("T25 FAIL: ok != true"); fails++; }
                var data = root.GetProperty("data");
                string[] required = { "uptimeSec", "com", "lan", "units", "overrides", "selfTest" };
                foreach (var k in required)
                    if (!data.TryGetProperty(k, out _))
                    { Console.WriteLine($"T25 FAIL: status missing '{k}'"); fails++; }
                string[] comKeys = { "streaming", "portOpen", "port", "baud", "linesSent", "linesPerSec", "template", "values" };
                foreach (var k in comKeys)
                    if (!data.GetProperty("com").TryGetProperty(k, out _))
                    { Console.WriteLine($"T25 FAIL: status.com missing '{k}'"); fails++; }
                string[] lanKeys = { "streaming", "adapterOpen", "framesSent", "framesPerSec", "equipment", "svCount", "targets" };
                foreach (var k in lanKeys)
                    if (!data.GetProperty("lan").TryGetProperty(k, out _))
                    { Console.WriteLine($"T25 FAIL: status.lan missing '{k}'"); fails++; }
            }
            catch (Exception ex)
            { Console.WriteLine($"T25 FAIL: status JSON unparseable: {ex.Message}"); fails++; }

            if (fails == 0)
                Console.WriteLine("T25 PASS: status JSON shape (uptime/com/lan/units/overrides keys present)");
            return fails;
        }

        // ── T26 ─────────────────────────────────────────────────────────
        private static int Test26_PipeEndToEnd()
        {
            int fails = 0;
            string pipeName = "iCemEmule.TestCtl." + Guid.NewGuid().ToString("N")[..8];
            var ctl = new FakeEmuControl();
            using var server = new ControlPipeServer(
                line => CommandProcessor.ProcessLine(line, ctl),
                () => CommandProcessor.ProcessLine("status", ctl).Json,
                pipeName);
            server.Start();

            try
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName, System.IO.Pipes.PipeDirection.InOut);
                client.Connect(5000);
                using var reader = new StreamReader(client);
                var writer = new StreamWriter(client) { AutoFlush = true };

                // status roundtrip
                writer.WriteLine("status");
                var reply = ReadWithTimeout(reader, 5000);
                if (reply is null || !ReplyOk(reply))
                { Console.WriteLine($"T26 FAIL: pipe status roundtrip: '{reply}'"); fails++; }

                // malformed over the pipe → JSON error, connection stays alive
                writer.WriteLine("{{{broken");
                var errReply = ReadWithTimeout(reader, 5000);
                if (errReply is null || ReplyOk(errReply) || !errReply.Contains("\"error\""))
                { Console.WriteLine($"T26 FAIL: pipe malformed handling: '{errReply}'"); fails++; }
                writer.WriteLine("status");
                var alive = ReadWithTimeout(reader, 5000);
                if (alive is null || !ReplyOk(alive))
                { Console.WriteLine("T26 FAIL: pipe connection dead after malformed input"); fails++; }

                // watch: ack + at least one 1 Hz tick
                writer.WriteLine("watch");
                var ack = ReadWithTimeout(reader, 5000);
                if (ack is null || !ReplyOk(ack))
                { Console.WriteLine($"T26 FAIL: watch ack: '{ack}'"); fails++; }
                var tick = ReadWithTimeout(reader, 4000);
                if (tick is null || !tick.Contains("\"uptimeSec\""))
                { Console.WriteLine($"T26 FAIL: watch tick missing/late: '{tick}'"); fails++; }
            }
            catch (Exception ex)
            { Console.WriteLine($"T26 FAIL: pipe end-to-end threw {ex.GetType().Name}: {ex.Message}"); fails++; }

            if (fails == 0)
                Console.WriteLine("T26 PASS: named-pipe end-to-end (status, malformed, watch tick)");
            return fails;
        }

        // ── helpers ─────────────────────────────────────────────────────
        private static bool ReplyOk(ControlReply r)
        {
            try
            {
                using var doc = JsonDocument.Parse(r.Json);
                return doc.RootElement.GetProperty("ok").GetBoolean();
            }
            catch { return false; }
        }

        private static bool ReplyOk(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("ok").GetBoolean();
            }
            catch { return false; }
        }

        private static string? ReadWithTimeout(StreamReader reader, int timeoutMs)
        {
            var task = reader.ReadLineAsync();
            return task.Wait(timeoutMs) ? task.Result : null;
        }

        // ── fake control for headless tests ─────────────────────────────
        private sealed class FakeEmuControl : IEmuControl
        {
            public readonly Dictionary<string, double> Vars = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Pump A Pressure"] = 100.0,
                ["Recirc Density"] = 15.8
            };
            private readonly Dictionary<string, double> _base = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Pump A Pressure"] = 100.0,
                ["Recirc Density"] = 15.8
            };
            public string? LastChannel;
            public int ComStarted, ComStopped, LanStarted, LanStopped;
            private bool _comOn, _lanOn;

            public Dictionary<string, object?> GetStatus() => new()
            {
                ["uptimeSec"] = 1.0,
                ["selfTest"] = null,
                ["com"] = new Dictionary<string, object?>
                {
                    ["streaming"] = _comOn, ["portOpen"] = _comOn, ["port"] = "COM2",
                    ["baud"] = 9600, ["linesSent"] = 0L, ["linesPerSec"] = 0.0,
                    ["template"] = "fake", ["values"] = new Dictionary<string, object?>()
                },
                ["lan"] = new Dictionary<string, object?>
                {
                    ["streaming"] = _lanOn, ["adapterOpen"] = _lanOn, ["adapter"] = "fake0",
                    ["framesSent"] = 0L, ["framesPerSec"] = 0.0, ["equipment"] = "0x00A86F46",
                    ["svCount"] = 6, ["targets"] = new List<object?> { "03:00:01:00:00:20" }
                },
                ["units"] = new Dictionary<string, object?> { ["preset"] = "LanNative" },
                ["overrides"] = new List<object?>()
            };

            public ControlResult StartChannel(string channel)
            {
                LastChannel = channel;
                if (channel == "com") { if (!_comOn) ComStarted++; _comOn = true; }
                else { if (!_lanOn) LanStarted++; _lanOn = true; }
                return ControlResult.Success();
            }

            public ControlResult StopChannel(string channel)
            {
                LastChannel = channel;
                if (channel == "com") { if (_comOn) ComStopped++; _comOn = false; }
                else { if (_lanOn) LanStopped++; _lanOn = false; }
                return ControlResult.Success();
            }

            public ControlResult SetVariable(string? channel, string name, double value, uint? sv)
            {
                LastChannel = channel;
                if (!Vars.ContainsKey(name))
                    return ControlResult.Fail($"variable '{name}' not found");
                Vars[name] = value;
                return ControlResult.Success();
            }

            public ControlResult ClearVariable(string? channel, string name, uint? sv)
            {
                if (!Vars.ContainsKey(name))
                    return ControlResult.Fail($"variable '{name}' not found");
                Vars[name] = _base[name]; // revert to base value
                return ControlResult.Success();
            }

            public ControlResult GetVariable(string? channel, string name, uint? sv)
            {
                if (!Vars.TryGetValue(name, out double v))
                    return ControlResult.Fail($"variable '{name}' not found");
                return ControlResult.Success(new Dictionary<string, object?> { ["name"] = name, ["value"] = v });
            }

            public ControlResult GetProfiles()
                => ControlResult.Success(new Dictionary<string, object?> { ["profiles"] = new List<object?>() });

            public ControlResult SetProfile(string? deviceType, string name)
                => ControlResult.Fail($"profile '{name}' not found");

            public ControlResult GetVariables(string? channel)
                => ControlResult.Success(new Dictionary<string, object?>());

            // ── W11.7 surface ────────────────────────────────────────────
            public ControlResult GetPorts()
                => ControlResult.Success(new Dictionary<string, object?> { ["ports"] = new List<object?> { "COM2" } });

            public ControlResult GetAdapters()
                => ControlResult.Success(new Dictionary<string, object?> { ["adapters"] = new List<object?> { "fake0" } });

            public ControlResult SetPort(string port) => ControlResult.Success(new Dictionary<string, object?> { ["port"] = port });
            public ControlResult SetAdapter(string adapter) => ControlResult.Success(new Dictionary<string, object?> { ["adapter"] = adapter });
            public ControlResult SetEquipment(uint equipment) => ControlResult.Success(new Dictionary<string, object?> { ["equipment"] = $"0x{equipment:X8}" });
            public ControlResult SetFirmware(bool on) => ControlResult.Success(new Dictionary<string, object?> { ["firmwareSv"] = on });
            public ControlResult SetEmulation(string channel, bool on) => ControlResult.Success(new Dictionary<string, object?> { ["channel"] = channel, ["emulating"] = on });
            public ControlResult SetPreset(int presetIndex) => ControlResult.Success(new Dictionary<string, object?> { ["preset"] = presetIndex });
            public ControlResult SetUnit(string group, string? unit) => ControlResult.Success(new Dictionary<string, object?> { ["group"] = group, ["unit"] = unit });

            public void RequestQuit() { }
        }
    }
}
