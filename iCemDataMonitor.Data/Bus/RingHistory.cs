using System.Collections.Concurrent;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// One addr-keyed ring sample — the {ts,v} shape served by the (restored)
/// GET /api/regs/history endpoint (Э2 web_endpoint_regs_history).
/// </summary>
/// <param name="TimestampUtc">Sample time (UTC — the sampler tick moment).</param>
/// <param name="Value">Decoded value: float32 for pairs 6..599; unix seconds
/// for the int32 raw-bits pair 4..5 (the float-bit reinterpretation of that
/// pair is garbage BY DECREE, CONTRACT v5.1).</param>
public readonly record struct RingSample(DateTimeOffset TimestampUtc, float Value);

/// <summary>
/// INV-32 (bus_pair_ring) ring storage — Э2 bus_pair_ring: "every float pair in
/// 4..599 has its own 600-entry (600 s @ 1 Hz) history keyed by absolute addr;
/// registry-free; ~300 pairs x 600 x 16 B &lt; 3 MB".
///
/// Pure in-memory store: ABSOLUTE addr → fixed 600-slot FIFO ring. No registry,
/// no variable names, no bus access — the sampler (<see cref="BusAddrRingService"/>)
/// owns the decode, the endpoint (web layer) owns the addr validation (even,
/// 4..599): the store accepts ANY int addr key (minimal surface).
///
/// Thread safety: one lock per address ring — concurrent AppendAddr/QueryAddr
/// never throw and never lose an append; QueryAddr returns an immutable copy,
/// oldest→newest (append order). Unknown addr → empty (never null, never throw).
///
/// Memory: each appended addr lazily allocates one 600 × 16 B buffer (9.6 KB);
/// the full 4..599 even-addr sweep (~298 pairs) stays under 3 MB. The entry at
/// position 0 is evicted FIFO on the 601st append to the same addr (600-entry
/// trim, falsifying tests RingHistoryTests.AppendAddr_*).
/// </summary>
public sealed class RingHistory
{
    /// <summary>Ring depth: 600 s @ 1 Hz (Э2 bus_pair_ring).</summary>
    public const int Capacity = 600;

    private readonly ConcurrentDictionary<int, AddrRing> _rings = new();

    /// <summary>Append one sample to the addr-keyed ring. The 601st append on
    /// the same addr evicts the oldest sample (FIFO trim). Any int addr accepted
    /// (domain validation of even/4..599 lives in the service/endpoint layers).</summary>
    public void AppendAddr(int addr, DateTimeOffset timestampUtc, float value) =>
        _rings.GetOrAdd(addr, static _ => new AddrRing(Capacity)).Append(timestampUtc, value);

    /// <summary>Immutable snapshot of the addr's ring, oldest→newest. Empty for
    /// an unknown addr (never null, never throws).</summary>
    public IReadOnlyList<RingSample> QueryAddr(int addr) =>
        _rings.TryGetValue(addr, out var ring) ? ring.Copy() : Array.Empty<RingSample>();

    /// <summary>Fixed-capacity circular buffer: overwrite-oldest when full.</summary>
    private sealed class AddrRing
    {
        private readonly RingSample[] _buf;
        private readonly object _gate = new();
        private int _head;   // oldest slot index
        private int _count;

        public AddrRing(int capacity) => _buf = new RingSample[capacity];

        public void Append(DateTimeOffset timestampUtc, float value)
        {
            lock (_gate)
            {
                int write = (_head + _count) % _buf.Length;
                _buf[write] = new RingSample(timestampUtc, value);
                if (_count < _buf.Length)
                {
                    _count++;
                }
                else
                {
                    _head = (_head + 1) % _buf.Length; // full: the write overwrote the oldest
                }
            }
        }

        public RingSample[] Copy()
        {
            lock (_gate)
            {
                var copy = new RingSample[_count];
                for (int i = 0; i < _count; i++)
                    copy[i] = _buf[(_head + i) % _buf.Length];
                return copy;
            }
        }
    }
}
