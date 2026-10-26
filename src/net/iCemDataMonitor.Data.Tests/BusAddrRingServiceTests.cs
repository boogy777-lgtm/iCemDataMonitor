using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// INV-32 BusAddrRingService pins (Э7: "addr-keyed sampler fills 4..599 pairs
/// (registry-free); smaller dictionary stops at word end"). Snapshots are built
/// DIRECTLY as records (coordinator-approved §15.7(a) data seam — no MMF): the
/// decode path under test (RegisterDecoder/CdabCodec) is pure span math, and the
/// file-seeded path is already pinned by BusReaderTests/BusRingServiceTests.
/// </summary>
public sealed class BusAddrRingServiceTests
{
    private const int UnixSeconds = 1756300000;   // pair 4..5 int32 raw bits (decode law)
    private static readonly DateTimeOffset Ts = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static BusSnapshot MakeSnapshot(params ushort[] words) => new(
        Magic: BusLayout.Magic,
        Version: BusLayout.Version,
        LayoutVersion: BusLayout.LayoutVersion,
        RegCount: (ushort)words.Length,
        Words: words,
        ComMain: default,
        Lan: default,
        RawReserve: default,
        Wits: default,
        Stale: false,
        CapturedAtTickMs: 0);

    /// <summary>Pair 4..5 = int32 unix raw bits: low word @addr, high @addr+1.</summary>
    private static void WriteUnixWords(ushort[] words, int addr)
    {
        words[addr] = (ushort)(UnixSeconds & 0xFFFF);
        words[addr + 1] = (ushort)(UnixSeconds >> 16);
    }

    /// <summary>CDAB float pair: reg[N]=low word, reg[N+1]=high word.</summary>
    private static void WriteFloat(ushort[] words, int addr, float value)
    {
        var (low, high) = CdabCodec.Split(value);
        words[addr] = low;
        words[addr + 1] = high;
    }

    [Fact]
    public void Ingest_FillsAllPairs4To599_RegistryFree()
    {
        // Full live body 0..599: pair 4..5 = int32 unix raw bits; every other
        // even pair = a distinctive float (addr + 0.25 — exact in float32).
        var words = new ushort[600];
        WriteUnixWords(words, 4);
        for (int addr = 6; addr <= 598; addr += 2)
            WriteFloat(words, addr, addr + 0.25f);

        var history = new RingHistory();
        var mapper = new BusAddrRingService(history);

        mapper.Ingest(MakeSnapshot(words), Ts);

        // Pair 4..5: int32 unix raw bits → ring value = unix seconds as float
        // (float-bit reinterpretation is garbage BY DECREE — RingSample doc).
        var pts4 = history.QueryAddr(4);
        Assert.Single(pts4);
        Assert.Equal(Ts, pts4[0].TimestampUtc);
        Assert.Equal((float)UnixSeconds, pts4[0].Value);

        // Every even pair 6..598 got exactly one point with the exact float.
        for (int addr = 6; addr <= 598; addr += 2)
        {
            var pts = history.QueryAddr(addr);
            Assert.True(pts.Count == 1, $"addr {addr}: expected 1 point, got {pts.Count}");
            Assert.Equal(addr + 0.25f, pts[0].Value);
            Assert.Equal(Ts, pts[0].TimestampUtc);
        }

        // 298 pairs total (even addrs 4..598) — the Э2 "~300 pairs" pin.
        Assert.Equal(298, (598 - BusAddrRingService.PairStart) / 2 + 1);

        // Odd addrs are pair TAILS, not keys — never ringed (registry-free
        // addr-keyed store).
        Assert.Empty(history.QueryAddr(5));
        Assert.Empty(history.QueryAddr(201));

        // The live body ends at 599: the ace_full reserve is never ringed.
        Assert.Empty(history.QueryAddr(600));
    }

    [Fact]
    public void Ingest_SmallerDictionary_StopsAtWordEnd()
    {
        // ── case 1: 10-word dictionary (indices 0..9) — pairs 4..5, 6..7, 8..9
        //    are complete; addr 10 has NO words at all ──
        var history = new RingHistory();
        var mapper = new BusAddrRingService(history);

        var ten = new ushort[10];
        WriteUnixWords(ten, 4);
        WriteFloat(ten, 6, 6.25f);
        WriteFloat(ten, 8, 8.25f);

        mapper.Ingest(MakeSnapshot(ten), Ts);

        Assert.Single(history.QueryAddr(4));   // unix pair recorded
        Assert.Single(history.QueryAddr(6));
        Assert.Single(history.QueryAddr(8));   // the last COMPLETE pair lands
        Assert.Empty(history.QueryAddr(10));   // beyond the dictionary — never ringed

        // ── case 2: 11-word dictionary (indices 0..10) — word 10 exists but its
        //    pair tail 11 does NOT: the incomplete pair is skipped (odd tail) ──
        var history2 = new RingHistory();
        var mapper2 = new BusAddrRingService(history2);

        var eleven = new ushort[11];
        WriteUnixWords(eleven, 4);
        eleven[10] = 0x1234;                   // orphan low word of pair 10..11

        mapper2.Ingest(MakeSnapshot(eleven), Ts);

        Assert.Single(history2.QueryAddr(4));  // 4..5 — the last COMPLETE pair
        Assert.Empty(history2.QueryAddr(10));  // incomplete pair (10, ·) — skipped
    }
}
