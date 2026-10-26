using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace iCemDataMonitor.Plugins.Icem;

// ── DTO types matching lantable_registry.json schema ──

public sealed record LantableSignalDef(
    int ByteOffset,
    int Size,
    string ValueType,
    double DasFactor,
    string Name,
    string Uom,
    int UnitClassId
);

public sealed record LantableVectorDef(
    uint SvNumber,
    string Name,
    string EquipmentType,
    IReadOnlyList<LantableSignalDef> Signals
);

public sealed record DecodedLanValue(
    string SignalName,
    double? Value,     // null for text-only types (UC, HX) and sentinel
    string? Text,      // for UC/HX types
    string Uom,
    bool IsSentinel    // true when FF/UL value == -9999.0
);

/// <summary>
/// Loads lantable_registry.json, provides O(1) lookup by service-vector number,
/// and decodes raw ACE frame signal payloads using the LantableNT XML definitions.
/// Sentinel -9999 is recognized and returned as IsSentinel=true with Value=null.
///
/// Templates parity: lantable_registry.json is SHA256-identical with the
/// FlecsEmu emitter copy. The firmware_registry.json merge (14 Beartooth RT
/// tables, FlecsEmu Core\LantableRegistry parity) is NOT loaded here — it is
/// part of the deferred ace_full work (future-work row in
/// specs/dotnet-server/Э2_contracts.csv; measured composition in
/// specs/dotnet-server/Э2b_ace_full_measurement.csv).
/// </summary>
public sealed class LantableRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public const double Sentinel = -9999.0;

    /// <summary>
    /// Real-wire signal-area base (frame offset 24). The sv u32 occupies
    /// signal-area slots 0..3 — all lantable signals have ByteOffset >= 4
    /// (min across 4386 signals = 4). Wire evidence (iCemCrio.pcapng.bin):
    /// Unit Number (o=4) @ frame 28, Mix Mode (o=12) @ frame 36, Pump A Pressure
    /// (o=4) @ frame 28 = 125.93 psi (golden T8), OPKO A Setting (o=12) = 150.0
    /// (golden T8b). Signal position = SignalBaseOffset + ByteOffset
    /// (= frame 0x18 + O — PROTOCOL.md §8 offset law).
    ///
    /// CONTRACT (merged emulator convention, wire-identical result): the
    /// emitter payload base sits at frame offset 0x1C(28)
    /// (FlecsEmu AceFrameBuilder.PayloadOffset = 0x1C) and AcePayloadProvider
    /// writes each signal to payload[ByteOffset − 4], landing it at
    /// frame 0x1C + O − 4 = 0x18 + O = SignalBaseOffset + O. An emitter/decoder
    /// pair disagreeing by 4 bytes (e.g. payload actually starting at 24, or a
    /// signal written at payload[ByteOffset]) shifts every decode — FF signals
    /// then read neighboring UL/SI/HX int bytes as float32, producing denormals
    /// whose bits equal the raw int (observed lan-zone defect:
    /// 1.4e-45 = 0x00000001, 1.13e-38 = 0x007C8000).
    /// </summary>
    public const int SignalBaseOffset = 24; // matched to AceFrameParser.SignalBaseOffset

    private readonly FrozenDictionary<uint, LantableVectorDef> _vectors;

    private LantableRegistry(FrozenDictionary<uint, LantableVectorDef> vectors)
    {
        _vectors = vectors;
    }

    public static LantableRegistry Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<LantableRegistryDocument>(json, JsonOpts)
                  ?? throw new InvalidOperationException("lantable_registry.json deserialized to null");

        var dict = new Dictionary<uint, LantableVectorDef>();
        foreach (var sv in doc.ServiceVectors!)
        {
            // First occurrence wins (matching LantableExtractor.TryAdd behaviour)
            dict.TryAdd(sv.SvNumber, new LantableVectorDef(
                SvNumber: sv.SvNumber,
                Name: sv.Name,
                EquipmentType: sv.EquipmentType,
                Signals: sv.Signals!.Select(s => new LantableSignalDef(
                    ByteOffset: s.ByteOffset,
                    Size: s.Size,
                    ValueType: s.ValueType,
                    DasFactor: s.DasFactor,
                    Name: s.Name,
                    Uom: s.Uom,
                    UnitClassId: s.UnitClassId
                )).ToList()
            ));
        }

        return new LantableRegistry(dict.ToFrozenDictionary());
    }

    public bool TryGet(uint svNumber, out LantableVectorDef def) =>
        _vectors.TryGetValue(svNumber, out def!);

    public int Count => _vectors.Count;

    /// <summary>All service-vector definitions (enumeration surface for tooling/tests — the runtime path is TryGet).</summary>
    public IReadOnlyCollection<LantableVectorDef> ServiceVectors => _vectors.Values;

    /// <summary>
    /// Decode signals from a raw ACE frame for a given service vector.
    /// Matches AceFrameParser.DecodeSignals semantics exactly:
    ///   FF → float32 big-endian × dasFactor; sentinel @ -9999.0
    ///   UL → uint32 big-endian × dasFactor; sentinel @ -9999.0
    ///   SL → int32 big-endian × dasFactor; sentinel @ -9999.0
    ///   SI → int32 big-endian × dasFactor; sentinel @ -9999.0
    ///   FD → float64 big-endian × dasFactor; sentinel @ -9999.0
    ///   UI → uint32 big-endian × dasFactor; sentinel @ -9999.0
    ///   US → uint16 big-endian × dasFactor; sentinel @ -9999.0
    ///   SS → int16 big-endian × dasFactor; sentinel @ -9999.0
    ///   SC → int8 × dasFactor; sentinel @ -9999.0
    ///   HX → hex string display only (no numeric value)
    ///   UC → ASCII null-terminated string
    /// </summary>
    public static List<DecodedLanValue> DecodeSignals(ReadOnlySpan<byte> frame, LantableVectorDef sv)
    {
        var result = new List<DecodedLanValue>(sv.Signals.Count);
        foreach (var sig in sv.Signals)
        {
            int abs = SignalBaseOffset + sig.ByteOffset;
            if (abs < 0 || abs + sig.Size > frame.Length)
            {
                result.Add(new DecodedLanValue(sig.Name, null, null, sig.Uom, false));
                continue;
            }

            switch (sig.ValueType)
            {
                case "FF":
                {
                    double v = BinaryPrimitives.ReadSingleBigEndian(frame.Slice(abs, 4)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "UL":
                {
                    double v = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(abs, 4)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "HX":
                {
                    uint raw = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(abs, 4));
                    result.Add(new DecodedLanValue(sig.Name, null,
                        string.Create(CultureInfo.InvariantCulture, $"0x{raw:X8}"), sig.Uom, false));
                    break;
                }
                case "SL":
                {
                    double v = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(abs, 4)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "SI":
                {
                    double v = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(abs, 4)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "FD":
                {
                    double v = ReadFloat64BigEndian(frame.Slice(abs, 8)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "UI":
                {
                    double v = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(abs, 4)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "US":
                {
                    double v = ReadUInt16BigEndian(frame.Slice(abs, 2)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "SS":
                {
                    double v = ReadInt16BigEndian(frame.Slice(abs, 2)) * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "SC":
                {
                    double v = (sbyte)frame[abs] * sig.DasFactor;
                    bool isSentinel = Math.Abs(v - Sentinel) < 0.001;
                    result.Add(new DecodedLanValue(sig.Name,
                        isSentinel ? null : v, null, sig.Uom, isSentinel));
                    break;
                }
                case "UC":
                {
                    var slice = frame.Slice(abs, sig.Size);
                    int nul = slice.IndexOf((byte)0);
                    if (nul < 0) nul = slice.Length;
                    string text = Encoding.ASCII.GetString(slice.Slice(0, nul));
                    result.Add(new DecodedLanValue(sig.Name, null, text, sig.Uom, false));
                    break;
                }
                default:
                    // All 11 ValueTypes present in lantable_registry.json are handled
                    // above (FF, UL, SL, SI, FD, UI, US, SS, SC, HX, UC) — measured
                    // 2026-08-17 (see specs/dotnet-server/Э2b_ace_full_measurement.csv).
                    result.Add(new DecodedLanValue(sig.Name, null, null, sig.Uom, false));
                    break;
            }
        }
        return result;
    }

    // ── Big-endian helpers for types not covered by BinaryPrimitives overloads ──

    private static short ReadInt16BigEndian(ReadOnlySpan<byte> frame)
        => (short)((frame[0] << 8) | frame[1]);

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> frame)
        => (ushort)((frame[0] << 8) | frame[1]);

    private static double ReadFloat64BigEndian(ReadOnlySpan<byte> frame)
    {
        // Reverse bytes for little-endian CPU
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = frame[7 - i];
        return BitConverter.ToDouble(bytes, 0);
    }

    // ── JSON DTO ──
    private sealed class LantableRegistryDocument
    {
        public int TotalEntries { get; set; }
        public int UniqueSvNumbers { get; set; }
        public List<LantableVectorDto>? ServiceVectors { get; set; }
    }

    private sealed class LantableVectorDto
    {
        public uint SvNumber { get; set; }
        public string Name { get; set; } = "";
        public string EquipmentType { get; set; } = "";
        public List<LantableSignalDto>? Signals { get; set; }
    }

    private sealed class LantableSignalDto
    {
        public int ByteOffset { get; set; }
        public int Size { get; set; }
        public string ValueType { get; set; } = "";
        public double DasFactor { get; set; }
        public string Name { get; set; } = "";
        public string Uom { get; set; } = "";
        public int UnitClassId { get; set; }
    }
}
