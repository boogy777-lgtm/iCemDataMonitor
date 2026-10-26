using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Telemetry;

/// <summary>
/// Shared absolute-address register decode law (W20 F.1) — ONE source for both
/// wire projections of the live bus: <see cref="MetricsSampler"/> RegisterDelta
/// (streaming deltas) and the unary GetRegisters projection (RegisterValue,
/// Host.Grpc). Semantics pinned by CONTRACT_BusV2 v5/v5.1:
///   - absolute addresses 0..599 are LIVE (header state regs 0..3 +
///     com_stream 4..199 / lan_stream_crio 200..399 / raw_com_line 400..599);
///   - words 0..3 are single u16 header state registers surfaced as int32;
///   - pair 4..5 carries UnixTimestampSeconds int32 RAW BITS in CDAB word
///     order (a float read of that pair is garbage BY DECREE);
///   - every other even-aligned pair is float32 via <see cref="CdabCodec"/>
///     (reg[N] = low word, reg[N+1] = high word).
/// Zero-allocation over the caller's span; no bus access here — callers own
/// the snapshot (sensor-at-send law).
/// </summary>
public static class RegisterDecoder
{
    /// <summary>Live end (exclusive): ace_full reserve starts here — nobody
    /// writes it in v5, so the live sweep stops at this address.</summary>
    public const int LiveEndExclusive = BusLayout.AceFullBaseReg;

    /// <summary>
    /// Decode ONE CDAB pair at an even absolute address (≥ HeaderRegCount).
    /// Returns (IsInt32=true, Int32Value=raw bits) for the timestamp pair at
    /// addr 4 and (false, _, FloatValue) for every other float pair.
    /// </summary>
    public static (bool IsInt32, int Int32Value, float FloatValue) DecodePair(ReadOnlySpan<ushort> words, int addr)
    {
        if (addr == BusLayout.HeaderRegCount)
        {
            // CONTRACT v5.1: com_stream idx0 (regs 4..5) = UnixTimestampSeconds
            // int32 RAW BITS in CDAB word order (reg[4] low word, reg[5] high word).
            int raw = words[addr] | (words[addr + 1] << 16);
            return (true, raw, 0f);
        }

        return (false, 0, CdabCodec.Combine(words[addr], words[addr + 1]));
    }
}
