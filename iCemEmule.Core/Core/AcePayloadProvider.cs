// AcePayloadProvider — IAcePayloadProvider implementation.
// Builds 124-byte payloads per sv in superframe using lantable signal definitions
// (legacy lantable_registry.json + firmware_registry.json merged by LantableRegistry)
// and simulation waveform values from variable_registry.json / firmware_variables.json.
// Encoding per ace_wire.signal_encoding: FF=float32BE, UL/SI=intBE with dasFactor,
// HX=u32BE, UC=ASCII NUL-pad, unknown=4 zeros.
// Offset law (real cRIO): сигнал с реестровым byteOffset O пишется на payload[O − 4]
// (кадр 0x18 + O; payload кадра начинается на 0x1C=28). Payload = 124 Б.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Halliburton.iCemEmule.Core;

public sealed class AcePayloadProvider : IAcePayloadProvider
{
    private readonly LantableRegistry _lantable;
    private readonly LanSimLookup _simLookup;

    // ── active sv numbers (contract ace_wire.superframe_svs) ──
    // Legacy 6 Cementer SV в прежнем порядке, затем 14 таблиц прошивки по возрастанию.
    // По умолчанию суперфрейм = только legacy (6 SV, как у реального cRIO-дампа);
    // 14 firmware-таблиц подключаются опцией IncludeFirmwareTables (чекбокс «Firmware SV»).
    private static readonly uint[] LegacySvNumbers =
    {
        0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020C, 0x1312020F
    };

    private static readonly uint[] FirmwareSvNumbers =
    {
        0x13120212, // RT Data A
        0x13120215, // RT Data B
        0x13120218, // RT Data C
        0x1312021B, // RT Totals & HPVT
        0x1312021E, // RT Observer A
        0x13120221, // RT Observer B
        0x13120224, // RT Bool A
        0x13120227, // RT Bool B
        0x1312022A, // RT Pump Life
        0x1312022D, // RT Blend & FSO
        0x13120230, // RT Strings
        0x13120233, // RT Bool DI Spares
        0x13120236, // RT Bool Relay & Misc
        0x13120239, // RT Spare AI/FI
    };

    /// <summary>Включать ли 14 firmware-таблиц в суперфрейм (по умолчанию выключено).</summary>
    public bool IncludeFirmwareTables { get; set; } = false;

    /// <summary>Текущий набор sv суперфрейма: 6 legacy, либо 6+14 при IncludeFirmwareTables.</summary>
    public IReadOnlyList<uint> ActiveSvNumbers
    {
        get
        {
            if (!IncludeFirmwareTables)
                return LegacySvNumbers;
            var all = new uint[LegacySvNumbers.Length + FirmwareSvNumbers.Length];
            Array.Copy(LegacySvNumbers, 0, all, 0, LegacySvNumbers.Length);
            Array.Copy(FirmwareSvNumbers, 0, all, LegacySvNumbers.Length, FirmwareSvNumbers.Length);
            return all;
        }
    }

    public AcePayloadProvider()
    {
        _lantable = new LantableRegistry();
        _simLookup = LoadSimLookup();
    }

    public AcePayloadProvider(LantableRegistry lantable)  // test seam
    {
        _lantable = lantable ?? new LantableRegistry();
        _simLookup = LoadSimLookup();
    }

    public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
    {
        var result = new List<AceVectorData>(ActiveSvNumbers.Count);
        foreach (var svNumber in ActiveSvNumbers)
        {
            var payload = BuildPayload(svNumber, elapsedSeconds);
            var svDef = _lantable.GetBySvNumber(svNumber);
            var name = svDef?.Name ?? $"SV_{svNumber:X8}";
            result.Add(new AceVectorData(svNumber, name, payload));
        }
        return result;
    }

    private byte[] BuildPayload(uint svNumber, double elapsed)
    {
        var buf = new byte[124]; // zeros by default (Array.Clear not needed for new)

        var svDef = _lantable.GetBySvNumber(svNumber);
        if (svDef is null) return buf;

        foreach (var sig in svDef.Signals)
        {
            // Закон раскладки (реальный cRIO, iCemCrio.pcapng.bin): сигнал с реестровым
            // byteOffset O лежит на payload[O − 4] (кадр 0x18 + O; payload кадра на 0x1C).
            // Все сигналы имеют O ≥ 4; (O−4)+Size ≤ 124 — иначе не помещается в payload.
            if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > 124)
                continue; // overflow guard

            var simEntry = _simLookup.Find(sig.Name);
            byte[] encoded;

            // UC-строки: если для сигнала задано stringValue — используем его 1:1 (NUL-pad).
            if (sig.ValueType.ToUpperInvariant() == "UC" && simEntry?.StringValue is not null)
            {
                encoded = EncodeAsciiNulPad(simEntry.StringValue, sig.Size);
            }
            else
            {
                double simValue = ResolveSignalValue(sig.Name, elapsed);
                encoded = EncodeSignal(simValue, sig.ValueType, sig.DasFactor, sig.Size);
            }

            if (encoded.Length > sig.Size)
                encoded = encoded.AsSpan(0, sig.Size).ToArray();

            Array.Copy(encoded, 0, buf, sig.ByteOffset - 4, Math.Min(encoded.Length, sig.Size));
        }

        return buf;
    }

    // ── signal → simulation value lookup ─────────────────────────────────
    private double ResolveSignalValue(string signalName, double elapsed)
    {
        var entry = _simLookup.Find(signalName);
        if (entry is null) return 0.0;

        return ComputeValue(entry, elapsed);
    }

    private static double ComputeValue(LanSimEntry e, double elapsed)
    {
        if (string.Equals(e.Format, "elapsed", StringComparison.OrdinalIgnoreCase))
            return elapsed;
        if (e.TogglePeriod > 0)
            return ((int)(elapsed / e.TogglePeriod) % 2) != 0 ? 1.0 : 0.0;
        if (e.Amplitude > 0 && e.Frequency > 0)
            return e.BaseValue + e.Amplitude * Math.Sin(2.0 * Math.PI * e.Frequency * elapsed);
        return e.BaseValue;
    }

    // ── signal encoding (BE, as per contract ace_wire.signal_encoding) ──
    private static byte[] EncodeSignal(double value, string valueType, double dasFactor, int size)
    {
        return valueType.ToUpperInvariant() switch
        {
            "FF" => EncodeFloat32BE((float)value),
            "UL" => EncodeUInt32BE((uint)Math.Round(value * dasFactor)),
            "UI" => EncodeUInt32BE((uint)Math.Round(value * dasFactor)),
            "SI" => EncodeInt32BE((int)Math.Round(value * dasFactor)),
            "SL" => EncodeInt32BE((int)Math.Round(value * dasFactor)),
            "HX" => EncodeUInt32BE((uint)Math.Round(value)), // hex=treat as uint
            "UC" => EncodeAsciiNulPad(value, size),
            _ => new byte[] { 0, 0, 0, 0 }
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] EncodeFloat32BE(float v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] EncodeUInt32BE(uint v)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] EncodeInt32BE(int v)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, v);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] EncodeAsciiNulPad(double value, int size)
    {
        var text = value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        return EncodeAsciiNulPad(text, size);
    }

    // UC-строка: ASCII-символы, остаток дополнен NUL (0x00)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] EncodeAsciiNulPad(string text, int size)
    {
        var buf = new byte[size];
        if (string.IsNullOrEmpty(text)) return buf;
        var enc = Encoding.ASCII;
        var count = Math.Min(text.Length, size);
        enc.GetBytes(text, 0, count, buf, 0);
        // rest is already zero (NUL-padded)
        return buf;
    }

    // ── variable_registry.json + firmware_variables.json → LanSimLookup ──
    private static LanSimLookup LoadSimLookup()
    {
        var lookup = new LanSimLookup();
        LoadSimFile(lookup, "variable_registry.json");
        LoadSimFile(lookup, "firmware_variables.json");
        return lookup;
    }

    private static void LoadSimFile(LanSimLookup lookup, string fileName)
    {
        // 2026-08-31: emulator-override law — variable_registry.emulator.json
        // wins when present (shared install root with the host, see
        // SimFileResolver.cs).
        var path = SimFileResolver.Resolve(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"), fileName);
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("variables", out var vars)) return;

            foreach (var v in vars.EnumerateArray())
            {
                var name = v.GetProperty("name").GetString() ?? string.Empty;
                var display = v.GetProperty("display").GetString() ?? string.Empty;
                var uom = v.TryGetProperty("uom", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
                bool exclude = v.TryGetProperty("excludeFromTelemetry", out var ex) && ex.GetBoolean();

                double b = 0, a = 0, f = 0, tp = 0;
                string fmt = string.Empty;
                if (v.TryGetProperty("simulation", out var sim))
                {
                    b = sim.TryGetProperty("base", out var sbase) ? sbase.GetDouble() : 0;
                    a = sim.TryGetProperty("amplitude", out var samp) ? samp.GetDouble() : 0;
                    f = sim.TryGetProperty("frequency", out var sfreq) ? sfreq.GetDouble() : 0;
                    fmt = sim.TryGetProperty("format", out var sfmt) ? (sfmt.GetString() ?? string.Empty) : string.Empty;
                    tp = sim.TryGetProperty("togglePeriod", out var stp) ? stp.GetDouble() : 0;
                }

                // UC-строки: stringValue вместо числовой симуляции
                string? stringValue = v.TryGetProperty("stringValue", out var sv) ? sv.GetString() : null;

                var entry = new LanSimEntry(name, display, uom, b, a, f, fmt, tp, exclude, stringValue);

                // check if variable has a "lan" family
                if (v.TryGetProperty("families", out var fams) &&
                    fams.TryGetProperty("lan", out var lanFam))
                {
                    var wireName = lanFam.TryGetProperty("wireName", out var wn)
                        ? (wn.GetString() ?? string.Empty) : string.Empty;
                    var alias = lanFam.TryGetProperty("alias", out var al)
                        ? (al.GetString() ?? string.Empty) : string.Empty;

                    if (!string.IsNullOrEmpty(wireName))
                        lookup.Add(wireName, entry);
                    if (!string.IsNullOrEmpty(alias) && !string.Equals(alias, wireName, StringComparison.OrdinalIgnoreCase))
                        lookup.Add(alias, entry);
                }

                // also index by variable name and display for broad matching
                if (!string.IsNullOrEmpty(name))
                    lookup.AddIfAbsent(name, entry);
                if (!string.IsNullOrEmpty(display) && !string.Equals(display, name, StringComparison.OrdinalIgnoreCase))
                    lookup.AddIfAbsent(display, entry);
            }
        }
        catch { /* best-effort; signal defaults to 0 */ }
    }

    // ── internal lookup types ────────────────────────────────────────────
    private sealed record LanSimEntry(
        string Name, string Display, string UoM,
        double BaseValue, double Amplitude, double Frequency,
        string Format, double TogglePeriod, bool ExcludeFromTelemetry,
        string? StringValue = null
    );

    private sealed class LanSimLookup
    {
        private readonly Dictionary<string, LanSimEntry> _map = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string key, LanSimEntry entry)
        {
            _map[key] = entry;
        }

        public void AddIfAbsent(string key, LanSimEntry entry)
        {
            if (!_map.ContainsKey(key))
                _map[key] = entry;
        }

        public LanSimEntry? Find(string signalName)
        {
            _map.TryGetValue(signalName, out var e);
            return e;
        }
    }
}
