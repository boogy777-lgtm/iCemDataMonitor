using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// INV-32 addr-keyed ring sampler — Э2 bus_pair_ring: "every float pair in
/// 4..599 has its own 600-entry (600 s @ 1 Hz) history keyed by absolute addr;
/// registry-free". Maps one <see cref="BusSnapshot"/> into <see cref="RingHistory"/>:
/// every even pair-start addr in 4..599 gets one AppendAddr per snapshot.
///
/// REGISTRY-FREE (the whole point of the addr-keyed ring): no variable names,
/// no UnifiedVariableRegistry — the only key is the absolute bus address.
///
/// Decode: the single decode law (W20 F.1) <see cref="RegisterDecoder.DecodePair"/> —
/// pair 4..5 carries int32 unix raw bits (low word @4, high word @5), and the
/// ring stores those UNIX SECONDS as float per <see cref="RingSample"/>: the
/// float-bit reinterpretation of that pair is garbage BY DECREE (CONTRACT v5.1).
/// Every other even pair is float32 CDAB via CdabCodec.
///
/// Dictionary bounds ("smaller dictionary stops at word end", Э7): the sweep
/// stops at the live-body end (600) AND at the last COMPLETE pair of THIS
/// dictionary — a snapshot whose word tail ends mid-pair does not ring the
/// orphan word.
/// </summary>
public sealed class BusAddrRingService
{
    /// <summary>First ringed addr = first post-header pair (4).</summary>
    public const int PairStart = BusLayout.HeaderRegCount;

    /// <summary>Ring end (exclusive) = live-body end (600) — the ace_full
    /// reserve 600..10000 is NEVER ringed (nobody writes it in v5).</summary>
    public const int PairEndExclusive = RegisterDecoder.LiveEndExclusive;

    private readonly RingHistory _history;

    /// <summary>The history this sampler fills (exposed for host composition —
    /// the same singleton the history endpoint queries).</summary>
    public RingHistory History { get; }

    public BusAddrRingService(RingHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
        History = history;
    }

    /// <summary>Ring every complete float pair 4..599 of the snapshot with the
    /// CURRENT time as the sample timestamp (production 1 Hz tick).</summary>
    public void Ingest(BusSnapshot snapshot) => Ingest(snapshot, DateTimeOffset.UtcNow);

    /// <summary>Ring every complete float pair 4..599 of the snapshot with an
    /// EXPLICIT tick timestamp (deterministic tests, injected time).</summary>
    public void Ingest(BusSnapshot snapshot, DateTimeOffset tickUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var words = new ushort[Math.Min(PairEndExclusive, snapshot.Words.Count)];
        snapshot.CopyWordsTo(0, words);
        // Same bounds discipline as MetricsSampler.ComputeDeltas: stop at the
        // live-body end AND at the last COMPLETE pair of THIS dictionary.
        for (int addr = PairStart;
             addr + 1 < PairEndExclusive && addr + 1 < words.Length;
             addr += 2)
        {
            var (isInt32, intValue, floatValue) = RegisterDecoder.DecodePair(words, addr);
            _history.AppendAddr(addr, tickUtc, isInt32 ? intValue : floatValue);
        }
    }
}
