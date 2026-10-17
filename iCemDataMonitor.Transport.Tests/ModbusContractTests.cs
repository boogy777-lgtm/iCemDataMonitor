using System.Net;
using System.Net.Sockets;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// Modbus contract tests (TZ_ARCH_V2 §5 layers.modbus + AC5, CONTRACT_BusV2 —
/// W3, layout v5 header state block). R5: the v1 family-layout tests (14000-reg
/// map, ITimeseriesStore mocks, per-family heartbeat echoes) are REPLACED,
/// ¬ fixed.
///
/// v5 header state block: regs 0..3 {comPortActive, caseState, caseLanState,
/// heartbeat@3} are an ACTIVE MIRROR ZONE — always bus-mirrored each tick,
/// external FC06/FC16 writes overwritten next tick. The block does NOT overlap
/// com_stream: ZoneBaseMin=4, the zone starts at reg 4 (the v4 4..15
/// overlap/skip rule is GONE) — zone probes below use HR 4+.
///
/// v5 zones (ALL AddIn-owned): com_stream 4..199, lan_stream_crio 200..399
/// (governed by the Lan liveness block — D4), raw_com_line 400..599 (float32
/// CDAB positional fields of the last CSV line — v5.1; u16 per char retired).
/// ace_full 600..10000 = DEFERRED reserve — the publisher NEVER
/// mirrors there; external FC06/FC16 writes persist forever.
///
/// Harness: a REAL persisted bus file (byte-exact per BusLayout — header
/// little-endian, body words big-endian) is seeded before the publisher starts;
/// the publisher's BusReader maps it. Ticks are driven deterministically via
/// ModbusTcpPublisher.TickOnce() (internal — InternalsVisibleTo); the
/// production tick loop (interval = options.TickMs, ini modbus_tick_ms) is
/// covered by short-interval loop tests (TickLoop_*).
///
/// Sizing law: the map edge is the DYNAMIC register file size
/// (ModbusTcpPublisher.RegisterCount — ini Total before the first snapshot, the
/// bus regCount afterwards). Boundary expectations derive from it, so the suite
/// pins the runtime behavior, ¬ a hardcoded 600/650/1650.
/// </summary>
public class ModbusContractTests
{
    private const int TestPort = 15502;

    // ═══ memcpy law: FC03 serves bus words VERBATIM (zero float work) ═══

    [Fact]
    public async Task FC03_ActiveZone_MirrorsBusWordsVerbatim()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetFloat(4, 15.79f)   // CDAB pin: [0xA3D7, 0x417C]
            .SetFloat(6, 1.0f));   // CDAB pin: [0x0000, 0x3F80]

        server.Tick();

        var (func, words) = await ReadRegistersAsync(0x03, 4, 4);
        Assert.Equal(0x03, func);
        Assert.Equal(new ushort[] { 0xA3D7, 0x417C, 0x0000, 0x3F80 }, words);

        // Byte-exact through the whole chain: file bytes → BusReader decode →
        // register file → FC03 wire. Decode must still yield the CDAB floats.
        Assert.Equal(15.79f, CdabCodec.Combine(words[0], words[1]), 3);
        Assert.Equal(1.0f, CdabCodec.Combine(words[2], words[3]));
    }

    [Fact]
    public async Task FC03_InactiveZone_NotMirrored_StaysZero()
    {
        // Probe at HR 4: the v5 state block is 0..3 ONLY (always mirrored) —
        // com_stream starts at reg 4 and the A5 hold rule applies from there
        // (v5 has NO state/zone overlap — the v4 4..15 overlap is GONE).
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: false)
            .SetFloat(4, 15.79f));

        server.Tick();

        var (_, words) = await ReadRegistersAsync(0x03, 4, 2);
        Assert.Equal(new ushort[] { 0, 0 }, words); // A5: inactive ⇒ bus NOT copied
    }

    [Fact]
    public async Task FC03_AllRegisters_Readable()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetZone(ZoneKind.Lan, active: true)
            .SetZone(ZoneKind.RawReserve, active: true));

        server.Tick();

        // FC03 chunks cover the FULL dynamic map HR 0..(RegisterCount-1) — the
        // edge moves with the runtime bus size (v3 sizing law).
        int count = server.Publisher.RegisterCount;
        Assert.Equal(BusLayout.RegCount, count); // seeded file ⇒ bus regCount applied
        for (int start = 0; start < count; start += 120)
        {
            ushort qty = (ushort)Math.Min(120, count - start);
            var (func, words) = await ReadRegistersAsync(0x03, (ushort)start, qty);
            Assert.Equal(0x03, func);
            Assert.Equal(qty, words.Length);
        }
    }

    [Fact]
    public async Task FC04_ReadInput_IsAliasOfHoldingFile()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetFloat(4, 15.79f));

        server.Tick();

        var (func, words) = await ReadRegistersAsync(0x04, 4, 2);
        Assert.Equal(0x04, func);
        Assert.Equal(new ushort[] { 0xA3D7, 0x417C }, words);
    }

    // ═══ header state block 0..3 (layout v5): always bus-mirrored — active mirror zone ═══

    [Fact]
    public async Task Header_Regs0To3_MirrorBusWords()
    {
        // Layout v5 state block: 0..3 = {comPortActive, caseState, caseLanState,
        // heartbeat} — ALL AddIn-owned (v5 has NO POC header writer).
        await using var server = await TestServer.CreateAsync(b => b
            .SetWord(BusLayout.RegComPort, 10)      // 0 — active COM port (D1 latch)
            .SetWord(BusLayout.RegCaseState, 3)     // 1 — iCem case state (tap enum 0..5)
            .SetWord(BusLayout.RegCaseLanState, 5)  // 2 — LAN/cRIO state (tap bits)
            .SetWord(BusLayout.RegHeartbeat, 42));  // 3 — heartbeat (v5 paradigm)

        server.Tick();

        var (_, words) = await ReadRegistersAsync(0x03, 0, 4);
        Assert.Equal(new ushort[] { 10, 3, 5, 42 }, words);

        // Heartbeat advances in the BUS (AddIn writes it) — publisher follows at HR 3.
        server.WriteBusFile(b => b.SetWord(3, 43));
        server.Tick();

        var (_, hb) = await ReadRegistersAsync(0x03, 3, 1);
        Assert.Equal(new ushort[] { 43 }, hb);
    }

    [Fact]
    public async Task Header_Heartbeat_Reg3Pin_V5Paradigm()
    {
        // Layout v5 POSITION PIN — frozen-history regression guard. v4: heartbeat
        // @reg 5, tied to Streaming. v5: heartbeat MOVED to reg 3 and decoupled
        // from Streaming — +1/s ALWAYS while the AddIn is loaded (pipeline-alive),
        // wraps 32000→0; frozen ⇔ iCem/AddIn dead (NOT ⇔ ¬Streaming). Any future
        // move = layoutVer bump + this pin + coordinated AddIn re-release
        // (CONTRACT_BusV2 §9 evolution).
        Assert.Equal(3, BusLayout.RegHeartbeat);
        Assert.Equal(4, BusLayout.HeaderRegCount);
        Assert.Equal(32000, BusLayout.HeartbeatWrap);

        // Behavioral pin: reg 3 mirrors VERBATIM from the bus — the publisher is
        // a pure memcpy; the +1/s cadence is the AddIn's, not the publisher's.
        await using var server = await TestServer.CreateAsync(b => b
            .SetWord(BusLayout.RegHeartbeat, 100));

        server.Tick();
        server.Tick();

        Assert.Equal(new ushort[] { 100 }, (await ReadRegistersAsync(0x03, 3, 1)).Words);
    }

    [Fact]
    public async Task Header_NoStateZoneOverlap_ComZoneStartsAt4()
    {
        // v5: the state block is 0..3 ONLY — com_stream starts at reg 4 with
        // NO overlap (the v4 4..15 overlap/skip rule is GONE). Consequence:
        // reg 4 is the first com_stream word and follows the ZONE flag (A4/A5),
        // unlike v4 where regs 4..15 followed the header mirror.
        Assert.Equal(4, BusLayout.ZoneBaseMin);
        Assert.Equal(4, BusLayout.ComStreamBaseReg);

        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: false)
            .SetWord(3, 0x0BB3)   // heartbeat — mirrors despite inactive zone
            .SetWord(4, 0xFFFF)); // first com_stream word — holds (zone inactive)

        server.Tick();

        Assert.Equal(new ushort[] { 0x0BB3 }, (await ReadRegistersAsync(0x03, 3, 1)).Words);
        Assert.Equal(new ushort[] { 0 }, (await ReadRegistersAsync(0x03, 4, 1)).Words); // A5 hold
    }

    [Fact]
    public async Task Header_NoBusEver_MirrorsZeros()
    {
        await using var server = await TestServer.CreateAsync(seed: null);

        server.Tick();

        // v5: no snapshot ever ⇒ header 0..3 = zeros; zone regs hold external
        // writes (no snapshot ⇒ no zone overwrite).
        Assert.Equal(0x06, (await WriteSingleAsync(4, 0xBEEF)).Func);
        server.Tick();

        var (_, words) = await ReadRegistersAsync(0x03, 0, 4);
        Assert.Equal(4, words.Length);
        Assert.All(words, w => Assert.Equal(0, w)); // header zeros, bus absent
        Assert.Equal(new ushort[] { 0xBEEF }, (await ReadRegistersAsync(0x03, 4, 1)).Words); // hold
    }

    // ═══ INC-11 observability pins: silent bus-absent degradation is FORBIDDEN ═══

    [Fact]
    public async Task OpenFail_ForeignShape_HoldsZeros_NoResize_RecordsError_SelfRecovers()
    {
        // INC-11 (2026-08-18): the stand served ALL ZEROS on :502 against a
        // provably healthy v5 bus — the tray ran STALE pre-migration binaries
        // whose shape gate rejected layoutVer=5/10001/20066 every tick; the
        // tick loop swallowed the failure silently ⇒ stuck bus-absent mode
        // (header zeros + hold) with zero diagnostics. This pin simulates the
        // exact signature: a file whose shape the gate REJECTS (valid magic,
        // layoutVer=5 claims 10001 regs, but the length is the v4-full
        // 16872 B — not a valid final shape, not an upgrade source).
        var foreign = new BusFileBuilder().SetWord(3, 42).Build(); // valid v5-full
        var skewed = new byte[V4FullFileBytes];                    // 16872 B only
        Array.Copy(foreign, skewed, skewed.Length);                // header claims 10001 regs

        await using var server = await TestServer.CreateAsync(
            seed: null, rawInput: skewed, total: V4FullRegCount);  // ini 8404 — the stand's stale ini
        Assert.Equal(V4FullRegCount, server.Publisher.RegisterCount); // ini dictionary pre-snapshot

        server.Tick();

        // bus-absent doctrine: header zeros, NO zone overwrite, NO resize —
        // and the failure is OBSERVABLE (the pre-INC-11 defect was the silence).
        Assert.All((await ReadRegistersAsync(0x03, 0, 4)).Words, w => Assert.Equal(0, w));
        Assert.Equal(V4FullRegCount, server.Publisher.RegisterCount); // rejected shape ⇒ no resize
        Assert.NotNull(server.Publisher.LastBusError);
        Assert.Contains("shape", server.Publisher.LastBusError);      // fail-safe wording surfaces

        // Self-recovery (no restart): a correct v5 file over the same path ⇒
        // the reader's per-tick retry opens it, the error clears, the register
        // file re-sizes to the runtime bus regCount, the mirror flows.
        server.WriteBusBytes(foreign);
        server.Tick();

        Assert.Null(server.Publisher.LastBusError);
        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);
        Assert.Equal(new ushort[] { 0, 0, 0, 42 }, (await ReadRegistersAsync(0x03, 0, 4)).Words);
    }

    [Fact]
    public async Task TickLoop_OpenFail_RecordsError_LoopSurvives_AndRecovers()
    {
        // Same INC-11 class through the PRODUCTION loop path (background
        // PeriodicTimer, ¬ manual ticks): the loop must keep ticking against
        // the absent bus, record the open failure per tick, and mirror
        // normally once the file appears — the tray-side recovery law.
        await using var server = await TestServer.CreateAsync(seed: null, tickMs: 200);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (server.Publisher.TickCount < 3)
            await Task.Delay(20, deadline.Token);
        Assert.NotNull(server.Publisher.LastBusError);
        Assert.Contains("not found", server.Publisher.LastBusError);

        server.WriteBusFile(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetWord(3, 7)
            .SetFloat(4, 1.0f)); // CDAB pin: [0x0000, 0x3F80]

        // Observe recovery through the TCP surface, not an assumed number
        // of scheduler ticks in a fixed sleep.
        while ((await ReadRegistersAsync(0x03, 3, 1)).Words[0] != 7)
            await Task.Delay(20, deadline.Token);
        Assert.Null(server.Publisher.LastBusError);
        Assert.Equal(new ushort[] { 7 }, (await ReadRegistersAsync(0x03, 3, 1)).Words);
        Assert.Equal(new ushort[] { 0x0000, 0x3F80 }, (await ReadRegistersAsync(0x03, 4, 2)).Words);
    }

    [Fact]
    public async Task FC06_HeaderStateWrite_OverwrittenByMirrorNextTick()
    {
        // v5: regs 0..3 = ACTIVE MIRROR ZONE. External FC06 writes into 0..3
        // are ACCEPTED (register file freely writable, A4) but the mirror
        // (bus = truth) overwrites them on the next tick (AC5-like verdict).
        await using var server = await TestServer.CreateAsync(b => b
            .SetWord(BusLayout.RegHeartbeat, 7));

        server.Tick();

        var (func, _) = await WriteSingleAsync(3, 999); // external write lands...
        Assert.Equal(0x06, func);
        var (_, landed) = await ReadRegistersAsync(0x03, 3, 1);
        Assert.Equal(new ushort[] { 999 }, landed);

        server.Tick(); // ...but the header mirror (bus = truth) wins

        var (_, mirrored) = await ReadRegistersAsync(0x03, 3, 1);
        Assert.Equal(new ushort[] { 7 }, mirrored);
    }

    [Fact]
    public async Task FC16_HeaderStateWrite_Accepted_OverwrittenByMirrorNextTick()
    {
        // FC16 into the whole v5 state block (0..3): accepted (A4), then
        // overwritten next tick — active mirror zone semantics, layout v5.
        await using var server = await TestServer.CreateAsync(b => b
            .SetWord(0, 0x0001).SetWord(1, 0x0002).SetWord(2, 0x0003).SetWord(3, 0x0004));

        server.Tick();

        Assert.Equal(0x10, (await WriteMultipleAsync(0, new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444 })).Func);
        Assert.Equal(new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444 },
            (await ReadRegistersAsync(0x03, 0, 4)).Words);

        server.Tick();

        Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004 },
            (await ReadRegistersAsync(0x03, 0, 4)).Words);
    }

    // ═══ PRODUCER-OWNED BUS (INV-39, owner decree 2026-08-27): the host is
    //     strictly READ-ONLY. A legacy v4-full 8404/16872 file is an UPGRADE
    //     SOURCE owned by the AddIn — the reader REJECTS it with an honest
    //     producer-migration error and NEVER touches the file (no in-place
    //     upgrade, no resize). Recovery = the producer (AddIn) migrates the
    //     file to layoutVer 5; the publisher follows on the next fresh
    //     snapshot (INC-11 recovery law unchanged). ═══

    /// <summary>v4-full dictionary (8404 regs) — the legacy shape the reader
    /// REJECTS under INV-39 (producer-owned bus: migration is AddIn-owned at
    /// BusWriter.Open; the host never rewrites a byte).</summary>
    private const int V4FullRegCount = 8404;
    private const int V4FullFileBytes = BusLayout.HeaderBytes + V4FullRegCount * 2; // 16872

    [Fact]
    public async Task V4FullFile_ProducerMigrationRequired_NoUpgrade_NoResize_RecordsError()
    {
        // INV-39 (owner decree 2026-08-27) — PRODUCER-OWNED BUS: the host is
        // strictly READ-ONLY. A v4-full 8404/16872 file is an UPGRADE SOURCE
        // owned by the AddIn (BusWriter.Open migrates under the bus mutex) —
        // the reader REJECTS it with an honest producer-migration error and
        // NEVER touches the file (no in-place upgrade, no resize). Recovery =
        // the producer (AddIn) migrates the file to layoutVer 5; the publisher
        // follows on the next fresh snapshot.
        var input = new BusFileBuilder()
            .SetWord(0, 10)          // comPortActive
            .SetWord(3, 42)          // heartbeat
            .SetWord(200, 0x0100)    // lan_stream_crio head
            .SetWord(V4FullRegCount - 1, 0xF00F)  // LAST v4 register (8403)
            .BuildLegacyV4Full();
        Assert.Equal(V4FullFileBytes, input.Length);
        Assert.Equal((ushort)4, BitConverter.ToUInt16(input, BusLayout.HeaderLayoutVersionOffset));

        await using var server = await TestServer.CreateAsync(seed: null, rawInput: input);
        server.Tick();

        // The reader NEVER touched the file: still 16872 B and BYTE-IDENTICAL
        // to the v4 input (FileShare.ReadWrite — the write path keeps a
        // ReadWrite handle; a rejected shape leaves the reader holding NO
        // handle at all).
        byte[] onDisk;
        using (var fs = new FileStream(server.MmfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            onDisk = new byte[fs.Length];
            fs.ReadExactly(onDisk);
        }
        Assert.Equal(V4FullFileBytes, onDisk.Length);
        Assert.Equal(input, onDisk);

        // Honest producer-migration error, surfaced each tick until a valid
        // v5 file appears (existing INC-11 recovery law unchanged).
        Assert.NotNull(server.Publisher.LastBusError);
        Assert.Contains("producer migration", server.Publisher.LastBusError);
        Assert.Contains("start iCem once", server.Publisher.LastBusError);

        // Bus-absent doctrine for the rejected shape: header reads all zeros,
        // no mirror (rejected shape ⇒ no snapshot ⇒ no copy).
        Assert.All((await ReadRegistersAsync(0x03, 0, 4)).Words, w => Assert.Equal(0, w));

        // The register file is sized from ini/defaults, NOT from the rejected
        // file: TestServer.CreateAsync(seed: null, rawInput:) leaves total
        // unset ⇒ ModbusOptions.Total = BusLayout.RegCount (10001).
        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);

        // Recovery: the producer migrates — a valid v5 file appears over the
        // same path; the next fresh snapshot clears the error and mirrors the
        // header (in-place recovery, no restart).
        server.WriteBusBytes(new BusFileBuilder().SetWord(0, 10).SetWord(3, 43).Build());
        server.Tick();

        Assert.Null(server.Publisher.LastBusError);
        Assert.Equal(new ushort[] { 10, 0, 0, 43 }, (await ReadRegistersAsync(0x03, 0, 4)).Words);
        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);
    }

    [Fact]
    public async Task BusGrowthBoundary_RegisterFileFollowsRuntimeBus()
    {
        // INV-39 growth-tolerance pin: the on-disk file may change shape while
        // this publisher stays open. Phase 1: a v4-full (8404/16872,
        // layoutVer=4) file appears — under the producer-owned-bus law the
        // reader REJECTS it as an upgrade source (migration is AddIn-owned;
        // the host never rewrites a byte). Phase 2: the producer's migrated
        // v5 10001/20066 file lands — the publisher follows on the next fresh
        // snapshot and clears the error. No crash in either phase.
        await using var server = await TestServer.CreateAsync(seed: null);

        // Phase 1: a v4-full-shaped bus appears (8404/16872, layoutVer=4) —
        // REJECTED as an upgrade source under INV-39.
        var v4 = new BusFileBuilder().SetWord(0, 5).SetWord(3, 43).BuildLegacyV4Full();
        server.WriteBusBytes(v4);
        server.Tick();

        // Bus-absent doctrine for the rejected shape: header zeros, honest
        // producer-migration error, file untouched on disk (still 16872 B).
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, (await ReadRegistersAsync(0x03, 0, 4)).Words);
        Assert.NotNull(server.Publisher.LastBusError);
        Assert.Contains("producer migration", server.Publisher.LastBusError);

        long onDiskLen;
        using (var fs = new FileStream(server.MmfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            onDiskLen = fs.Length;
        Assert.Equal(V4FullFileBytes, onDiskLen);

        // The register file keeps the DEFAULT dictionary size (ini/defaults —
        // TestServer leaves total unset ⇒ BusLayout.RegCount = 10001): the
        // last v4 register (8403) is INSIDE the writable map and FC06
        // succeeds — the size basis is the configured dictionary, NOT the
        // rejected file.
        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);
        Assert.Equal((0x06, 0x00), await WriteSingleAsync((ushort)(V4FullRegCount - 1), 1)); // last v4 reg legal

        // Phase 2: the producer's migration lands — a valid v5 10001/20066
        // file while the publisher stays open.
        server.WriteBusBytes(new BusFileBuilder().SetWord(0, 5).SetWord(3, 44).Build());
        server.Tick();

        // The register map serves the FULL dictionary: edge at 10001, error
        // cleared, header mirror flows from the migrated bus.
        Assert.Null(server.Publisher.LastBusError);
        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);
        Assert.Equal((0x06, 0x00), await WriteSingleAsync((ushort)BusLayout.AceFullEndReg, 7));
        Assert.Equal(new ushort[] { 7 },
            (await ReadRegistersAsync(0x03, (ushort)BusLayout.AceFullEndReg, 1)).Words);
        Assert.Equal((0x86, 0x02), await WriteSingleAsync((ushort)BusLayout.RegCount, 1));
        Assert.Equal(new ushort[] { 44 }, (await ReadRegistersAsync(0x03, 3, 1)).Words); // header mirror from the migrated bus
    }

    // ═══ A5 + AC5: inactive zone write persists; active zone write is overwritten ═══

    [Fact]
    public async Task FC06_InactiveZone_WritePersistsAcrossTicks()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: false)
            .SetWord(100, 0x1234)); // bus value present but zone INACTIVE

        server.Tick();

        // Zone inactive ⇒ bus word at 100 is NOT mirrored.
        var (_, before) = await ReadRegistersAsync(0x03, 100, 1);
        Assert.Equal(new ushort[] { 0 }, before);

        // External FC06 write lands in the register file.
        var (func, _) = await WriteSingleAsync(100, 0xABCD);
        Assert.Equal(0x06, func);
        Assert.Equal(new ushort[] { 0xABCD }, (await ReadRegistersAsync(0x03, 100, 1)).Words);

        server.Tick();
        server.Tick();
        server.Tick();

        // A5: inactive zone untouched by ticks ⇒ external write persists indefinitely.
        Assert.Equal(new ushort[] { 0xABCD }, (await ReadRegistersAsync(0x03, 100, 1)).Words);
    }

    [Fact]
    public async Task FC06_ActiveZone_WriteOverwrittenOnNextTick()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetWord(4, 0x1111));

        server.Tick();
        Assert.Equal(new ushort[] { 0x1111 }, (await ReadRegistersAsync(0x03, 4, 1)).Words);

        // External write into the ACTIVE zone lands immediately...
        Assert.Equal(0x06, (await WriteSingleAsync(4, 0xBEEF)).Func);
        Assert.Equal(new ushort[] { 0xBEEF }, (await ReadRegistersAsync(0x03, 4, 1)).Words);

        server.Tick(); // ...but the bus (source of truth) wins on the next tick (AC5).

        Assert.Equal(new ushort[] { 0x1111 }, (await ReadRegistersAsync(0x03, 4, 1)).Words);
    }

    [Fact]
    public async Task FC16_WriteMultiple_InactiveZone_Persists()
    {
        // v5.1 raw_com_line zone head (400..599, float32 CDAB positional fields): inactive ⇒ hold.
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.RawReserve, active: false)
            .SetWord(400, 0xAAAA));

        server.Tick();

        var (func, _) = await WriteMultipleAsync(400, new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004 });
        Assert.Equal(0x10, func);

        server.Tick();
        server.Tick();

        Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004 },
            (await ReadRegistersAsync(0x03, 400, 4)).Words);
    }

    [Fact]
    public async Task FC16_WriteMultiple_ActiveZone_Overwritten()
    {
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.RawReserve, active: true)
            .SetWord(400, 0xAAAA).SetWord(401, 0xBBBB)
            .SetWord(402, 0xCCCC).SetWord(403, 0xDDDD));

        server.Tick();

        Assert.Equal(0x10, (await WriteMultipleAsync(400, new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444 })).Func);
        Assert.Equal(new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444 },
            (await ReadRegistersAsync(0x03, 400, 4)).Words);

        server.Tick();

        Assert.Equal(new ushort[] { 0xAAAA, 0xBBBB, 0xCCCC, 0xDDDD },
            (await ReadRegistersAsync(0x03, 400, 4)).Words);
    }

    // ═══ ace_full reserve (HR 600..10000 — layout v5 DEFERRED; nobody writes) ═══

    [Fact]
    public async Task AceFullReserve_NeverMirrored_ExternalWritesPersist()
    {
        // v5: ace_full 600..10000 is a DEFERRED reserve — NO zone, NO writer,
        // NO publisher mirror row (supersedes the v4 wits 600..649
        // reserved-dead tests: even a bus value present is never copied).
        Assert.Equal(600, BusLayout.AceFullBaseReg);
        Assert.Equal(10000, BusLayout.AceFullEndReg);

        await using var server = await TestServer.CreateAsync(b => b
            .SetWord(600, 0x1234)); // bus value present (foreign writer sim)

        server.Tick();

        // Reserve never mirrored — the bus word at 600 does NOT appear.
        var (_, before) = await ReadRegistersAsync(0x03, 600, 1);
        Assert.Equal(new ushort[] { 0 }, before);

        // External FC06 write at HR 600 lands and persists indefinitely.
        var (func, _) = await WriteSingleAsync(600, 0xABCD);
        Assert.Equal(0x06, func);
        Assert.Equal(new ushort[] { 0xABCD }, (await ReadRegistersAsync(0x03, 600, 1)).Words);

        server.Tick();
        server.Tick();
        server.Tick();

        Assert.Equal(new ushort[] { 0xABCD }, (await ReadRegistersAsync(0x03, 600, 1)).Words);
    }

    [Fact]
    public async Task AceFullReserve_EndOfMap_Readable_Writable()
    {
        // The reserve is inside the readable map: the last register (10000)
        // answers FC03/FC06 — the dynamic map edge follows the bus regCount.
        await using var server = await TestServer.CreateAsync(seed: null);
        server.Tick();

        Assert.Equal(BusLayout.AceFullEndReg, server.Publisher.RegisterCount - 1);
        Assert.Equal((0x06, 0x00), await WriteSingleAsync((ushort)BusLayout.AceFullEndReg, 7));
        Assert.Equal(new ushort[] { 7 },
            (await ReadRegistersAsync(0x03, (ushort)BusLayout.AceFullEndReg, 1)).Words);
    }

    // ═══ lan_stream_crio zone (layout v5: HR 200..399, governed by the Lan
    //     liveness block — D4, writer = AddIn) ═══

    [Fact]
    public async Task LanStreamCrio_LanLiveness_MirrorsAndHolds()
    {
        // v5 geometry: lan_stream_crio 200..399 (100 float32 CDAB pairs;
        // addr=200+idx*2). The Lan liveness block (tick@0x13(19), flag@0x1B(27),
        // counter@0x29(41)) governs it — D4, writer = AddIn (v5 has no POC
        // writer). The v4 lan 200..449 + lan_ext 650..8403 shared-liveness pair
        // is REPLACED by this single zone.
        Assert.Equal(200, BusLayout.LanStreamCrioBaseReg);
        Assert.Equal(200, BusLayout.LanStreamCrioRegCount);

        int lastPairAddr = BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount - 2; // 398

        // Lan activeFlag=1 ⇒ lan_stream_crio mirrors. Seed head (200) + tail (398).
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.Lan, active: true)
            .SetFloat(BusLayout.LanStreamCrioBaseReg, 1.5f)   // 200
            .SetFloat(lastPairAddr, -2.25f));                 // 398/399

        server.Tick();

        var (func, words) = await ReadRegistersAsync(0x03, (ushort)BusLayout.LanStreamCrioBaseReg, 2);
        Assert.Equal(0x03, func);
        Assert.Equal(1.5f, CdabCodec.Combine(words[0], words[1]));

        var (_, tail) = await ReadRegistersAsync(0x03, (ushort)lastPairAddr, 2);
        Assert.Equal(-2.25f, CdabCodec.Combine(tail[0], tail[1]));

        // Lan goes inactive ⇒ the D4 flag = 0 ⇒ lan_stream_crio HOLDS: the
        // previous mirror survives and external FC06 writes persist (A5).
        server.WriteBusFile(b => b
            .SetZone(ZoneKind.Lan, active: false)
            .SetFloat(BusLayout.LanStreamCrioBaseReg, 9.5f)); // bus changes but zone inactive
        server.Tick();
        server.Tick();

        // Hold, ¬ overwrite: the bus now carries 9.5f but the register file keeps 1.5f.
        var (_, held) = await ReadRegistersAsync(0x03, (ushort)BusLayout.LanStreamCrioBaseReg, 2);
        Assert.Equal(1.5f, CdabCodec.Combine(held[0], held[1]));

        // External FC06 into the inactive lan_stream_crio range persists indefinitely.
        Assert.Equal(0x06, (await WriteSingleAsync(300, 0xBEEF)).Func);
        server.Tick();
        server.Tick();
        Assert.Equal(new ushort[] { 0xBEEF }, (await ReadRegistersAsync(0x03, 300, 1)).Words);
    }

    // ═══ v3 sizing law: register file follows the RUNTIME bus regCount ═══

    [Fact]
    public async Task Sizing_RegisterFileReshapesToRuntimeBusRegCount()
    {
        // Bus ABSENT + ini total = 650 (legacy-sized config): the register file
        // starts at the CONFIGURED dictionary — map edge at 650.
        await using var server = await TestServer.CreateAsync(seed: null, total: 650);

        Assert.Equal(650, server.Publisher.RegisterCount);
        Assert.Equal((0x06, 0x00), await WriteSingleAsync(649, 1)); // last reg OK
        Assert.Equal((0x86, 0x02), await WriteSingleAsync(650, 1)); // beyond map

        // Bus appears with the FULL dictionary (header regCount self-describing):
        // the register file re-shapes to the RUNTIME bus size — bus = truth.
        server.WriteBusFile(b => b
            .SetZone(ZoneKind.Lan, active: true)
            .SetWord(200, 0x0100));
        server.Tick();

        Assert.Equal(BusLayout.RegCount, server.Publisher.RegisterCount);
        Assert.Equal((0x06, 0x00), await WriteSingleAsync((ushort)(BusLayout.RegCount - 1), 1)); // 10000 OK (ace_full end)
        Assert.Equal((0x86, 0x02), await WriteSingleAsync((ushort)BusLayout.RegCount, 1));       // 10001 beyond
    }

    // ═══ bus-absent / stale: registers hold, server keeps serving (AC3/AC4) ═══

    [Fact]
    public async Task BusAbsent_RegistersHold_ServerAnswers_ThenReconnect()
    {
        // No bus file at all — publisher starts, serves zeros, accepts writes.
        await using var server = await TestServer.CreateAsync(seed: null);

        server.Tick();
        Assert.Equal(new ushort[] { 0, 0 }, (await ReadRegistersAsync(0x03, 4, 2)).Words);

        // External write holds across ticks while the bus is absent (no overwrite).
        Assert.Equal(0x06, (await WriteSingleAsync(200, 0x00FF)).Func);
        server.Tick();
        server.Tick();
        Assert.Equal(new ushort[] { 0x00FF }, (await ReadRegistersAsync(0x03, 200, 1)).Words);

        // Bus appears (AddIn creates the file) ⇒ active zone overwrite resumes,
        // header mirror activates (reader re-tries TryOpen on every Snapshot).
        server.WriteBusFile(b => b
            .SetZone(ZoneKind.Lan, active: true)
            .SetWord(200, 0x0100)
            .SetWord(0, 5));
        server.Tick();

        Assert.Equal(new ushort[] { 0x0100 }, (await ReadRegistersAsync(0x03, 200, 1)).Words);
        Assert.Equal(new ushort[] { 5 }, (await ReadRegistersAsync(0x03, 0, 1)).Words);
    }

    [Fact]
    public async Task StaleSnapshot_NoZoneOverwrite_HeaderStillMirrorsLastGood()
    {
        // Zone probe at HR 4 — OUTSIDE the v5 state block 0..3: the header
        // mirror (always, even stale — last-good state words) does NOT touch it,
        // so the stale-hold verdict (AC3/AC4) is observable on the zone head.
        await using var server = await TestServer.CreateAsync(b => b
            .SetZone(ZoneKind.ComMain, active: true)
            .SetWord(0, 7)
            .SetWord(4, 0x2222));

        server.Tick();
        Assert.Equal(new ushort[] { 0x2222 }, (await ReadRegistersAsync(0x03, 4, 1)).Words);

        // Hold the bus mutex from a dedicated thread ⇒ the tick's snapshot is
        // STALE ⇒ no zone overwrite. Latch-based (¬ sleep): the holder keeps the
        // mutex until release.Set() AFTER the stale tick — deterministic under load.
        using var gate = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holderTask = Task.Run(() =>
        {
            using var holder = new Mutex(false, server.MutexName);
            holder.WaitOne();
            gate.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            holder.ReleaseMutex(); // REQUIRED: dispose-without-release would leave the
                                   // kernel mutex owned by the (alive) pool thread forever
        });
        gate.Wait();

        try
        {
            Assert.Equal(0x06, (await WriteSingleAsync(4, 0x9999)).Func);
            server.Tick(); // blocks ~500 ms, snapshot stale ⇒ zone write SURVIVES
            Assert.Equal(new ushort[] { 0x9999 }, (await ReadRegistersAsync(0x03, 4, 1)).Words);

            // Header mirror still writes the last-good bus words during staleness.
            Assert.Equal(new ushort[] { 7 }, (await ReadRegistersAsync(0x03, 0, 1)).Words);
        }
        finally
        {
            release.Set();
            await holderTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Mutex released ⇒ fresh snapshot ⇒ active zone overwrite resumes.
        server.Tick();
        Assert.Equal(new ushort[] { 0x2222 }, (await ReadRegistersAsync(0x03, 4, 1)).Words);
    }

    // ═══ error semantics: illegal function / address / value ═══

    [Fact]
    public async Task FC03_AddressBounds_IllegalDataAddress()
    {
        await using var server = await TestServer.CreateAsync(seed: null);
        server.Tick();

        int count = server.Publisher.RegisterCount;
        Assert.Equal((0x83, 0x02), await ReadRegistersRawAsync(0x03, (ushort)(count - 1), 2)); // crosses last reg
        Assert.Equal((0x83, 0x02), await ReadRegistersRawAsync(0x03, (ushort)count, 1));       // beyond map
    }

    [Fact]
    public async Task FC03_QuantityBounds_IllegalDataValue()
    {
        await using var server = await TestServer.CreateAsync(seed: null);
        server.Tick();

        Assert.Equal((0x83, 0x03), await ReadRegistersRawAsync(0x03, 0, 0));   // qty 0
        Assert.Equal((0x83, 0x03), await ReadRegistersRawAsync(0x03, 0, 126)); // qty > 125
    }

    [Fact]
    public async Task FC06_AddressBounds()
    {
        await using var server = await TestServer.CreateAsync(seed: null);
        server.Tick();

        int count = server.Publisher.RegisterCount;
        Assert.Equal((0x86, 0x02), await WriteSingleAsync((ushort)count, 1));       // beyond map
        Assert.Equal((0x06, 0x00), await WriteSingleAsync((ushort)(count - 1), 1)); // last reg OK
    }

    [Fact]
    public async Task FC16_ErrorCases()
    {
        await using var server = await TestServer.CreateAsync(seed: null);
        server.Tick();

        int count = server.Publisher.RegisterCount;
        Assert.Equal((0x90, 0x02), await WriteMultipleAsync((ushort)(count - 1), new ushort[] { 1, 2 })); // crosses last reg
        Assert.Equal((0x90, 0x03), await WriteMultipleAsync(0, Array.Empty<ushort>()));                    // qty 0
        Assert.Equal((0x90, 0x03), await WriteMultipleAsync(0, new ushort[124]));                          // qty > 123
        Assert.Equal((0x90, 0x03), await WriteMultipleRawAsync(450, 2, byteCount: 3));                     // byteCount mismatch
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x05)]
    [InlineData(0x0F)]
    public async Task IllegalFunction_ReturnsException01(byte funcCode)
    {
        await using var server = await TestServer.CreateAsync(seed: null);

        var (func, exc) = await RawFuncAsync(funcCode, new byte[] { 0, 0, 0, 1 });
        Assert.Equal((byte)(funcCode | 0x80), func);
        Assert.Equal(0x01, exc);
    }

    // ═══ production loop: periodic tick (config-driven cadence) ═══

    [Fact]
    public async Task TickLoop_CopiesActiveZone_Periodically()
    {
        await using var server = await TestServer.CreateAsync(
            b => b.SetZone(ZoneKind.ComMain, active: true).SetWord(4, 0x3333),
            tickInterval: TimeSpan.FromMilliseconds(25));

        // NO manual TickOnce — the background loop must deliver the bus word.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ushort[] words = Array.Empty<ushort>();
        while (DateTime.UtcNow < deadline)
        {
            words = (await ReadRegistersAsync(0x03, 4, 1)).Words;
            if (words[0] == 0x3333)
                break;
            await Task.Delay(25);
        }

        Assert.Equal(new ushort[] { 0x3333 }, words);
    }

    [Fact]
    public async Task TickLoop_IntervalFollowsOptionsTickMs()
    {
        // options.TickMs = 200 (NO ctor override) ⇒ the background loop must
        // tick ~5×/s — the EXTERNAL config drives the cadence, ¬ the old 1 Hz.
        // Observable via the internal TickCount counter (refresh-thread per-tick
        // increment, InternalsVisibleTo).
        await using var server = await TestServer.CreateAsync(
            b => b.SetZone(ZoneKind.ComMain, active: true).SetWord(4, 0x3333),
            tickMs: 200);

        long before = server.Publisher.TickCount;
        await Task.Delay(1100);
        long ticks = server.Publisher.TickCount - before;

        Assert.InRange(ticks, 3, 8); // ~5.5 expected @200 ms; ±CI slack, ¬ 1 Hz
    }

    [Fact]
    public async Task TickLoop_ActiveZoneWrite_OverwrittenEveryCycle_NoDeltaDetection()
    {
        // 200 ms cadence: an external FC06 write into the ACTIVE zone is
        // overwritten within a few cycles — PLAIN overwrite every cycle,
        // Δ-based change detection BANNED (operator directive).
        await using var server = await TestServer.CreateAsync(
            b => b.SetZone(ZoneKind.ComMain, active: true).SetWord(4, 0x1111),
            tickMs: 200);

        // The background loop (no manual TickOnce) delivers the bus word.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ushort[] words = Array.Empty<ushort>();
        while (DateTime.UtcNow < deadline)
        {
            words = (await ReadRegistersAsync(0x03, 4, 1)).Words;
            if (words[0] == 0x1111)
                break;
            await Task.Delay(20);
        }
        Assert.Equal(new ushort[] { 0x1111 }, words);

        // External write lands immediately...
        Assert.Equal(0x06, (await WriteSingleAsync(4, 0xBEEF)).Func);
        Assert.Equal(new ushort[] { 0xBEEF }, (await ReadRegistersAsync(0x03, 4, 1)).Words);

        // ...but the bus (source of truth) wins within ≤ ~2 ticks @200 ms.
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            words = (await ReadRegistersAsync(0x03, 4, 1)).Words;
            if (words[0] == 0x1111)
                break;
            await Task.Delay(20);
        }
        Assert.Equal(new ushort[] { 0x1111 }, words);
    }

    [Fact]
    public void ModbusOptions_TickMs_Default_Is500()
    {
        Assert.Equal(500, new ModbusOptions().TickMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_TickMsBelow1_Throws(int tickMs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "modbus_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var reader = new BusReader(
                Path.Combine(dir, "test_bus.dat"),
                $"iCemDataMonitorBusTestMap_{Guid.NewGuid():N}",
                $"iCemDataMonitorBusTestMutex_{Guid.NewGuid():N}");
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ModbusTcpPublisher(reader, new ModbusOptions { TickMs = tickMs }));
            Assert.Contains("TickMs", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Modbus TCP client helpers ──

    private static async Task<byte[]> SendPduAsync(byte[] pdu)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TestPort);
        using var stream = client.GetStream();

        ushort tid = 1, pid = 0;
        byte unitId = 1;
        ushort length = (ushort)(1 + pdu.Length);
        var full = new byte[7 + pdu.Length]; // MBAP(6) + unitId(1) + pdu
        full[0] = (byte)((tid >> 8) & 0xFF);
        full[1] = (byte)(tid & 0xFF);
        full[2] = (byte)((pid >> 8) & 0xFF);
        full[3] = (byte)(pid & 0xFF);
        full[4] = (byte)((length >> 8) & 0xFF);
        full[5] = (byte)(length & 0xFF);
        full[6] = unitId;
        Array.Copy(pdu, 0, full, 7, pdu.Length);

        await stream.WriteAsync(full);
        await stream.FlushAsync();

        var mbap = new byte[6];
        int read = 0;
        while (read < 6)
        {
            int r = await stream.ReadAsync(mbap.AsMemory(read, 6 - read));
            if (r == 0) throw new EndOfStreamException();
            read += r;
        }

        ushort rspLength = (ushort)((mbap[4] << 8) | mbap[5]);
        var body = new byte[rspLength];
        read = 0;
        while (read < rspLength)
        {
            int r = await stream.ReadAsync(body.AsMemory(read, rspLength - read));
            if (r == 0) throw new EndOfStreamException();
            read += r;
        }
        return body; // unitId + func(+data)
    }

    private static async Task<(byte Func, ushort[] Words)> ReadRegistersAsync(byte funcCode, ushort startAddr, ushort qty)
    {
        var pdu = new byte[5];
        pdu[0] = funcCode;
        pdu[1] = (byte)(startAddr >> 8);
        pdu[2] = (byte)(startAddr & 0xFF);
        pdu[3] = (byte)(qty >> 8);
        pdu[4] = (byte)(qty & 0xFF);

        var body = await SendPduAsync(pdu);
        if ((body[1] & 0x80) != 0)
            return (body[1], Array.Empty<ushort>());

        var words = new ushort[qty];
        for (int i = 0; i < qty; i++)
            words[i] = (ushort)((body[3 + i * 2] << 8) | body[3 + i * 2 + 1]);
        return (body[1], words);
    }

    private static async Task<(byte Func, byte Exception)> ReadRegistersRawAsync(byte funcCode, ushort startAddr, ushort qty)
    {
        var pdu = new byte[5];
        pdu[0] = funcCode;
        pdu[1] = (byte)(startAddr >> 8);
        pdu[2] = (byte)(startAddr & 0xFF);
        pdu[3] = (byte)(qty >> 8);
        pdu[4] = (byte)(qty & 0xFF);

        var body = await SendPduAsync(pdu);
        if ((body[1] & 0x80) != 0)
            return (body[1], body[2]);
        return (body[1], 0);
    }

    private static async Task<(byte Func, byte Exception)> WriteSingleAsync(ushort addr, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = 0x06;
        pdu[1] = (byte)(addr >> 8);
        pdu[2] = (byte)(addr & 0xFF);
        pdu[3] = (byte)(value >> 8);
        pdu[4] = (byte)(value & 0xFF);

        var body = await SendPduAsync(pdu);
        return (body[1], (body[1] & 0x80) != 0 ? body[2] : (byte)0);
    }

    private static async Task<(byte Func, byte Exception)> WriteMultipleAsync(ushort addr, ushort[] values)
    {
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = 0x10;
        pdu[1] = (byte)(addr >> 8);
        pdu[2] = (byte)(addr & 0xFF);
        pdu[3] = (byte)(values.Length >> 8);
        pdu[4] = (byte)(values.Length & 0xFF);
        pdu[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            pdu[6 + i * 2] = (byte)(values[i] >> 8);
            pdu[6 + i * 2 + 1] = (byte)(values[i] & 0xFF);
        }

        var body = await SendPduAsync(pdu);
        return (body[1], (body[1] & 0x80) != 0 ? body[2] : (byte)0);
    }

    private static async Task<(byte Func, byte Exception)> WriteMultipleRawAsync(ushort addr, ushort qty, byte byteCount)
    {
        var pdu = new byte[7 + byteCount];
        pdu[0] = 0x10;
        pdu[1] = (byte)(addr >> 8);
        pdu[2] = (byte)(addr & 0xFF);
        pdu[3] = (byte)(qty >> 8);
        pdu[4] = (byte)(qty & 0xFF);
        pdu[5] = byteCount;
        // data bytes left zero — byteCount intentionally inconsistent with qty

        var body = await SendPduAsync(pdu);
        return (body[1], (body[1] & 0x80) != 0 ? body[2] : (byte)0);
    }

    private static async Task<(byte Func, byte Exception)> RawFuncAsync(byte funcCode, byte[] data)
    {
        var pdu = new byte[1 + data.Length];
        pdu[0] = funcCode;
        Array.Copy(data, 0, pdu, 1, data.Length);

        var body = await SendPduAsync(pdu);
        return (body[1], (body[1] & 0x80) != 0 ? body[2] : (byte)0);
    }

    // ── Harness ──

    /// <summary>Per-test server: temp bus file + publisher over it, manual ticks.</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly BusReader _reader;
        private readonly CancellationTokenSource _cts = new();

        public ModbusTcpPublisher Publisher { get; }
        public string MmfPath { get; }
        public string MapName { get; }
        public string MutexName { get; }

        private TestServer(string dir, string mmfPath, string mapName, string mutexName,
            ModbusTcpPublisher publisher, BusReader reader)
        {
            _dir = dir;
            MmfPath = mmfPath;
            MapName = mapName;
            MutexName = mutexName;
            Publisher = publisher;
            _reader = reader;
        }

        /// <summary>
        /// Creates the server. seed (optional) writes the persisted bus file BEFORE
        /// the publisher starts; null = bus file absent. Default tick interval is
        /// 1 h so the background loop is INERT in manual-tick tests. total (optional)
        /// overrides ModbusOptions.Total (ini total) — the configured dictionary
        /// size used before the first snapshot. mutateFile (optional)
        /// post-processes the built file bytes before write (contract pins, e.g.
        /// forcing a specific layoutVer). rawInput (optional) writes EXACT bytes
        /// (e.g. a true v4-full 8404/16872 INPUT — see
        /// V4FullFile_ProducerMigrationRequired_NoUpgrade_NoResize_RecordsError);
        /// when given, seed and mutateFile are ignored. tickMs (optional) sets
        /// ModbusOptions.TickMs AND disables the ctor-interval override ⇒ the
        /// background loop runs at the EXTERNAL-config cadence (tick-law tests).
        /// </summary>
        public static async Task<TestServer> CreateAsync(Action<BusFileBuilder>? seed = null, TimeSpan? tickInterval = null, int? total = null, Action<byte[]>? mutateFile = null, int? tickMs = null, byte[]? rawInput = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "modbus_test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var mmfPath = Path.Combine(dir, "test_bus.dat");
            var mapName = $"iCemDataMonitorBusTestMap_{Guid.NewGuid():N}";
            var mutexName = $"iCemDataMonitorBusTestMutex_{Guid.NewGuid():N}";

            if (rawInput != null)
            {
                File.WriteAllBytes(mmfPath, rawInput);
            }
            else if (seed != null)
            {
                var b = new BusFileBuilder();
                seed(b);
                var bytes = b.Build();
                mutateFile?.Invoke(bytes);
                File.WriteAllBytes(mmfPath, bytes);
            }

            var reader = new BusReader(mmfPath, mapName, mutexName);
            var options = new ModbusOptions { Port = TestPort, Total = total ?? BusLayout.RegCount };
            if (tickMs is { } t)
                options.TickMs = t;
            var publisher = new ModbusTcpPublisher(reader, options,
                tickMs is { } ? null : tickInterval ?? TimeSpan.FromHours(1));

            await publisher.StartAsync();
            await Task.Delay(150); // listener bind settle

            return new TestServer(dir, mmfPath, mapName, mutexName, publisher, reader);
        }

        public void Tick() => Publisher.TickOnce();

        /// <summary>
        /// Rewrite the bus file mid-test (AddIn update simulation). In-place
        /// overwrite with FileShare.ReadWrite — the reader's persisted mapping
        /// keeps a ReadWrite handle (BusMapping), so a plain File.WriteAllBytes
        /// (FileShare.Read) would hit a sharing violation.
        /// </summary>
        public void WriteBusFile(Action<BusFileBuilder> seed)
        {
            var b = new BusFileBuilder();
            seed(b);
            WriteBusBytes(b.Build());
        }

        /// <summary>Write EXACT bytes to the persisted bus file mid-test
        /// (FileShare-safe). Used for shape-boundary simulations: a rejected
        /// v4-full 8404/16872 input, then the producer-migrated 10001/20066
        /// file — the file changes shape on disk while the publisher stays
        /// open.</summary>
        public void WriteBusBytes(byte[] bytes)
        {
            using var fs = new FileStream(MmfPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            fs.Position = 0;
            fs.Write(bytes, 0, bytes.Length);
            fs.SetLength(bytes.Length); // grows/shrinks the persisted file to the exact shape
            fs.Flush(true);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await Publisher.StopAsync();
            Publisher.Dispose();
            _reader.Dispose();
            _cts.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Byte-exact persisted bus file builder (BusLayout geometry — header
    /// little-endian, per-zone flags, body words big-endian). The zone header
    /// blocks are written DATA-DRIVEN over the v5 mirror zones ONLY (ComMain,
    /// Lan, RawReserve). LanExt has no zone in v5 (absorbed into the ace_full
    /// reserve) and the Wits block offsets (49..63) are RESERVED-ZERO by law —
    /// the build loop skips BOTH so the reserved bytes stay zero in the
    /// persisted file. SetZone(LanExt, …) delegates to the Lan flag (v4
    /// compile-compat only — no v5 test uses it).
    /// </summary>
    private sealed class BusFileBuilder
    {
        private static readonly ZoneKind[] Kinds = Enum.GetValues<ZoneKind>();

        private readonly ushort[] _words = new ushort[BusLayout.RegCount];
        private readonly bool[] _active = new bool[Kinds.Length]; // indexed by ZoneKind

        public BusFileBuilder SetZone(ZoneKind kind, bool active)
        {
            // LanExt SHARES the Lan liveness byte (v4 shared-liveness history) —
            // a lan_ext activation IS a lan activation in the persisted file.
            _active[(int)(kind == ZoneKind.LanExt ? ZoneKind.Lan : kind)] = active;
            return this;
        }

        public BusFileBuilder SetWord(int addr, ushort value)
        {
            _words[addr] = value;
            return this;
        }

        public BusFileBuilder SetFloat(int addr, float value)
        {
            var (low, high) = CdabCodec.Split(value);
            _words[addr] = low;
            _words[addr + 1] = high;
            return this;
        }

        public byte[] Build()
        {
            var buf = new byte[BusLayout.TotalFileBytes];
            BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
            BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
            BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
            BitConverter.GetBytes(BusLayout.RegCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);

            long nowTick = Environment.TickCount64;
            foreach (var kind in Kinds)
            {
                if (kind == ZoneKind.LanExt || kind == ZoneKind.Wits)
                    continue; // LanExt: no v5 zone; Wits: reserved-zero block (49..63 stays zero)
                BitConverter.GetBytes(nowTick).CopyTo(buf, BusLayout.ZoneLastWriteTickOffset(kind));
                buf[BusLayout.ZoneActiveFlagOffset(kind)] = _active[(int)kind] ? (byte)1 : (byte)0;
            }

            for (int i = 0; i < BusLayout.RegCount; i++)
            {
                int off = BusLayout.BodyOffset + i * 2;
                buf[off] = (byte)(_words[i] >> 8);
                buf[off + 1] = (byte)(_words[i] & 0xFF);
            }
            return buf;
        }

        /// <summary>
        /// Crafts a TRUE v4-full INPUT file (8404 regs / 16872 B, layoutVer=4)
        /// — the legacy shape the reader REJECTS under INV-39 (producer-owned
        /// bus, owner decree 2026-08-27): it is an upgrade source OWNED by the
        /// AddIn, and the host never rewrites it. Only words [0..8403] exist;
        /// seeded words beyond that bound are dropped. Zone liveness blocks use
        /// the same v5 offsets (com/lan/raw; Wits/LanExt skipped).
        /// </summary>
        public byte[] BuildLegacyV4Full()
        {
            var buf = new byte[64 + V4FullRegCount * 2];
            BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
            BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
            BitConverter.GetBytes(BusLayout.LayoutVersionV4).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
            BitConverter.GetBytes((ushort)V4FullRegCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);

            long nowTick = Environment.TickCount64;
            foreach (var kind in Kinds)
            {
                if (kind == ZoneKind.LanExt || kind == ZoneKind.Wits)
                    continue;
                BitConverter.GetBytes(nowTick).CopyTo(buf, BusLayout.ZoneLastWriteTickOffset(kind));
                buf[BusLayout.ZoneActiveFlagOffset(kind)] = _active[(int)kind] ? (byte)1 : (byte)0;
            }

            for (int i = 0; i < V4FullRegCount; i++)
            {
                int off = BusLayout.BodyOffset + i * 2;
                buf[off] = (byte)(_words[i] >> 8);
                buf[off + 1] = (byte)(_words[i] & 0xFF);
            }
            return buf;
        }
    }
}
