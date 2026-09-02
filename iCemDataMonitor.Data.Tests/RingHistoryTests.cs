using System.Collections.Concurrent;
using iCemDataMonitor.Data.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// INV-32 RingHistory pins (Э7 RingHistoryTests: "per-address tracking,
/// 600-entry trim, unknown-addr empty"; falsifying tests RingHistoryTests.AppendAddr_*).
/// Pure in-memory — no bus, no MMF.
/// </summary>
public sealed class RingHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    // ═══ 1. per-address tracking: two addrs never mix ═══

    [Fact]
    public void AppendAddr_PerAddressTracking_AddressesDoNotMix()
    {
        var h = new RingHistory();
        h.AppendAddr(4, T0, 1f);
        h.AppendAddr(4, T0.AddSeconds(1), 2f);
        h.AppendAddr(200, T0, 99f);

        var a4 = h.QueryAddr(4);
        var a200 = h.QueryAddr(200);
        Assert.Equal(2, a4.Count);
        Assert.Single(a200);
        Assert.Equal(99f, a200[0].Value);

        // More traffic on 200 leaves 4 untouched (addr-keyed, no cross-talk).
        h.AppendAddr(200, T0.AddSeconds(2), 100f);
        h.AppendAddr(200, T0.AddSeconds(3), 101f);
        Assert.Equal(2, h.QueryAddr(4).Count);
        Assert.Equal(3, h.QueryAddr(200).Count); // 99f + 100f + 101f
    }

    // ═══ 2. 600-entry trim: the 601st append evicts the oldest (FIFO) ═══

    [Fact]
    public void AppendAddr_600EntryTrim_OldestEvicted()
    {
        var h = new RingHistory();
        for (int i = 1; i <= RingHistory.Capacity + 1; i++)
            h.AppendAddr(4, T0.AddSeconds(i), i);

        var pts = h.QueryAddr(4);
        Assert.Equal(RingHistory.Capacity, pts.Count);          // capped at 600
        Assert.Equal(2, pts[0].Value);                          // sample #1 evicted (FIFO)
        Assert.Equal(T0.AddSeconds(2), pts[0].TimestampUtc);
        Assert.Equal(RingHistory.Capacity + 1, pts[pts.Count - 1].Value); // newest = 601st
        Assert.Equal(T0.AddSeconds(RingHistory.Capacity + 1), pts[pts.Count - 1].TimestampUtc);
    }

    // ═══ 3. unknown addr → empty (never null, never throw) ═══

    [Fact]
    public void QueryAddr_UnknownAddr_Empty()
    {
        var h = new RingHistory();
        var pts = h.QueryAddr(999);
        Assert.NotNull(pts);
        Assert.Empty(pts);

        // Appends to OTHER addrs keep the unknown addr empty (per-address keys).
        h.AppendAddr(4, T0, 1f);
        Assert.Empty(h.QueryAddr(999));
    }

    // ═══ 4. order: oldest → newest (append order) ═══

    [Fact]
    public void QueryAddr_OrderedOldestToNewest()
    {
        var h = new RingHistory();
        for (int i = 0; i < 5; i++)
            h.AppendAddr(6, T0.AddSeconds(i), i);

        var pts = h.QueryAddr(6);
        Assert.Equal(5, pts.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(T0.AddSeconds(i), pts[i].TimestampUtc); // exact sequence
            Assert.Equal(i, pts[i].Value);
        }
    }

    // ═══ 5. timestamp preserved exactly (tick precision) ═══

    [Fact]
    public void AppendAddr_TimestampPreserved_Exactly()
    {
        var h = new RingHistory();
        var ts = T0.AddTicks(1234567); // sub-millisecond precision survives
        h.AppendAddr(10, ts, 1f);

        Assert.Equal(ts, h.QueryAddr(10)[0].TimestampUtc);
    }

    // ═══ 6. float value preserved exactly (incl. extremes/NaN) ═══

    [Fact]
    public void AppendAddr_FloatValuePreserved_Exactly()
    {
        var h = new RingHistory();
        float[] values =
        {
            15.79f, -42.0f, float.MaxValue, float.MinValue,
            float.PositiveInfinity, float.NegativeInfinity, float.NaN, 0f,
        };
        for (int i = 0; i < values.Length; i++)
            h.AppendAddr(12, T0.AddSeconds(i), values[i]);

        var pts = h.QueryAddr(12);
        Assert.Equal(values.Length, pts.Count);
        for (int i = 0; i < values.Length; i++)
            Assert.Equal(values[i], pts[i].Value); // xUnit Equal handles NaN
    }

    // ═══ 7. thread-safety smoke: parallel append+query — no throw, no corruption ═══

    [Fact]
    public async Task AppendAddr_ConcurrentAppendQuery_NoLossNoThrow()
    {
        var h = new RingHistory();
        const int writers = 4;
        const int perWriter = 500;                  // 4 × 500 = 2000 appends > Capacity — trim under the hammer

        var queryErrors = new ConcurrentQueue<Exception>();
        using var cts = new CancellationTokenSource();

        // Query threads: every mid-flight snapshot must be internally consistent —
        // unique values, count ≤ capacity, ts↔value invariant (value == ms offset).
        var queryTasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var pts = h.QueryAddr(7);
                    var seen = new HashSet<float>();
                    Assert.True(pts.Count <= RingHistory.Capacity, "ring exceeded capacity mid-flight");
                    foreach (var p in pts)
                    {
                        Assert.True(p.TimestampUtc == T0.AddMilliseconds(p.Value),
                            "ts↔value invariant violated");
                        Assert.True(seen.Add(p.Value), "duplicate value in one query snapshot");
                    }
                }
            }
            catch (Exception ex)
            {
                queryErrors.Enqueue(ex);
            }
        })).ToArray();

        // Writer threads: value v carries its own timestamp T0 + v ms, so the
        // ts↔value invariant holds regardless of the interleave order.
        var appendTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                int v = w * perWriter + i;
                h.AppendAddr(7, T0.AddMilliseconds(v), v);
            }
        })).ToArray();

        await Task.WhenAll(appendTasks).WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Task.WhenAll(queryTasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(queryErrors);

        // Deterministic end state (per-addr lock): the ring holds EXACTLY the
        // last Capacity appends — no exception, no corruption, no lost slot.
        var final = h.QueryAddr(7);
        Assert.Equal(RingHistory.Capacity, final.Count);
        Assert.Equal(RingHistory.Capacity, final.Select(p => (int)p.Value).Distinct().Count());
    }
}
