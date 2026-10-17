using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// Per-zone bus liveness info parsed from the 64-byte header (CONTRACT_BusV2 §2).
/// </summary>
/// <param name="Kind">Zone identity (header block index ≡ ZoneKind value).</param>
/// <param name="LastWriteTicks">DateTime.UtcNow.Ticks at the zone writer's last write — UTC ticks,
/// age computable across reboot (CONTRACT_BusV2 §2, ¬ TickCount64).</param>
/// <param name="Active">activeFlag — 1 = zone rewritten every second by its writer (A4/A5).</param>
/// <param name="WriteCount">Wrapping producer counter (diagnostic only).</param>
public readonly record struct BusZoneInfo(ZoneKind Kind, long LastWriteTicks, bool Active, uint WriteCount)
{
    /// <summary>
    /// W10.1 zone-liveness gate (ONE constant for EVERY consumer surface —
    /// health monitor, REST mirrors, truth-table rows): the activeFlag is the
    /// writer's OWN claim — it can stay 1 forever after a writer death. The
    /// contract says writers tick every 1 s, so a zone whose last write is
    /// older than 5 s is NOT active no matter what the stale flag claims
    /// (lamps must follow truth). Consumers MUST NOT re-declare their own
    /// 5000 ms constant — this one is the law.
    /// </summary>
    public const long MaxZoneAgeMs = 5_000;

    /// <summary>Age of the zone's last write in ms (staleness metric; clamped at 0).</summary>
    public long AgeMs() => AgeMs(DateTime.UtcNow);

    /// <summary>Age at an explicit UTC instant; shares the production age calculation.</summary>
    public long AgeMs(DateTime utcNow)
    {
        long age = (utcNow.Ticks - LastWriteTicks) / TimeSpan.TicksPerMillisecond;
        return age >= 0 ? age : 0;
    }

    /// <summary>
    /// Age-gated liveness verdict: <c>Active &amp;&amp; AgeMs() &lt; MaxZoneAgeMs</c>.
    /// STALE-DECAY LAW (2026-08-28): this verdict is RECOMPUTABLE from ANY
    /// snapshot — fresh or STALE. A frozen last-good copy still carries REAL
    /// zone ticks, and AgeMs() is measured against UtcNow, so the verdict decays
    /// to false as real time passes even when the snapshot never refreshes
    /// (mutex-timeout / file-absent degrade, BUS-LIFECYCLE rows #10/#11/#12) —
    /// a dead writer can never freeze a «live» verdict. The raw writer claim
    /// stays available as <see cref="Active"/> (diagnostic surface).
    /// </summary>
    public bool IsActive => IsActiveAt(DateTime.UtcNow);

    /// <summary>Age-gated verdict at the same instant used to report the age.</summary>
    public bool IsActiveAt(DateTime utcNow) => Active && AgeMs(utcNow) < MaxZoneAgeMs;
}

/// <summary>
/// Immutable point-in-time copy of the whole bus (64 B header + body).
/// Header fields little-endian; Words[] big-endian-decoded u16 registers.
/// Copy happens UNDER the mutex; decode happens outside (CONTRACT_BusV2 §5).
/// </summary>
/// <param name="Magic">'STNB' as LE u32 (0x424E5453) — 0 when never read.</param>
/// <param name="Version">Bus protocol version.</param>
/// <param name="LayoutVersion">Header layout version (v5).</param>
/// <param name="RegCount">Holding-register count (RUNTIME DATA from header; current default 10001).</param>
/// <param name="Words">regCount × u16, byte-exact Modbus-wire copy (big-endian decoded).</param>
/// <param name="ComMain">com_stream zone header block (liveness).</param>
/// <param name="Lan">lan_stream_crio zone header block (D4: the Lan liveness block governs lan_stream_crio, AddIn-owned).</param>
/// <param name="RawReserve">raw_com_line zone header block (liveness).</param>
/// <param name="Wits">former wits zone header block (reserved-zero in the 64-byte file header — reads as never-written).</param>
/// <param name="Stale">True when the mutex was not acquired within the timeout ⇒ this is a LAST-GOOD copy.</param>
/// <param name="CapturedAtTickMs">Environment.TickCount64 when the copy was taken.</param>
public sealed record BusSnapshot(
    uint Magic,
    ushort Version,
    ushort LayoutVersion,
    ushort RegCount,
    ushort[] Words,
    BusZoneInfo ComMain,
    BusZoneInfo Lan,
    BusZoneInfo RawReserve,
    BusZoneInfo Wits,
    bool Stale,
    long CapturedAtTickMs)
{
    /// <summary>
    /// Body header-state register (int16 BE word at body reg `reg`) — guarded:
    /// 0 on dictionaries shorter than the register. Only the 4 LIVE state regs
    /// are surfaced (v5 map: header block 0..3 {comPortActive, caseState,
    /// caseLanState, heartbeat}; the v4 regs 4..15 are com_stream float pairs
    /// now and are NOT exposed as state).
    /// </summary>
    private short Hdr(int reg) => reg < Words.Length ? unchecked((short)Words[reg]) : (short)0;

    // ── LIVE AddIn-owned state regs (v5 map — 4 regs) ──
    /// <summary>reg 0 — comPortActive u16: N of COMN currently feeding; 0=none. D1 first-valid-wins latch. Writer: AddIn.</summary>
    public short ComPort => Hdr(BusLayout.RegComPort);

    /// <summary>reg 1 — caseState u16: iCem case state; tap-exposed values ONLY; unknown=0. Writer: AddIn.</summary>
    public short CaseState => Hdr(BusLayout.RegCaseState);

    /// <summary>reg 2 — caseLanState u16: LAN/cRIO state; tap-exposed values ONLY; unknown=0. Writer: AddIn.</summary>
    public short CaseLanState => Hdr(BusLayout.RegCaseLanState);

    /// <summary>reg 3 — heartbeat u16, +1/s ALWAYS while the AddIn is loaded (v5 paradigm:
    /// pipeline-alive signal, NOT Streaming-gated; wraps 32000→0). Frozen ⇔ iCem/AddIn dead.
    /// Writer: AddIn.</summary>
    public short Heartbeat => Hdr(BusLayout.RegHeartbeat);

    /// <summary>FROZEN v4 surface (compile-compat only — DO NOT USE): reg 8 WAS vendor
    /// LAN status — v5 carries LAN state at header reg 2 (<see cref="CaseLanState"/>);
    /// regs 8..10 are com_stream float pairs now. Reading this returns the low word
    /// of com_stream pair idx 2 — GARBAGE rendered as LAN state (dishonest-surface
    /// incident 2026-08-28: gRPC/Shell/REST all read this as case_lan_state; every
    /// consumer was rewired to <see cref="CaseLanState"/>). Kept ONLY so a stale
    /// external binary fails to compile loudly instead of silently re-adopting it.</summary>
    [Obsolete("v4 frozen surface: reg 8 is a com_stream float-pair low word in v5 — " +
              "use CaseLanState (header reg 2, BusLayout.RegCaseLanState) for the honest LAN state", error: false)]
    public short LanStatus => Hdr(BusLayout.RegLanStatus);

    /// <summary>Per-zone info by kind (header block order ≡ ZoneKind order).
    /// v5: the Lan liveness block governs lan_stream_crio (D4, AddIn-owned);
    /// LanExt aliases the Lan block for v4 compile-compat until the
    /// Plugins/Transport agents rewire (shared-liveness law).</summary>
    public BusZoneInfo ZoneInfo(ZoneKind kind) => kind switch
    {
        ZoneKind.ComMain => ComMain,
        ZoneKind.Lan => Lan,
        ZoneKind.LanExt => Lan,
        ZoneKind.RawReserve => RawReserve,
        ZoneKind.Wits => Wits,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ZoneKind"),
    };

    /// <summary>Raw register word by absolute address. Valid range:
    /// 0..RegCount−1 of THIS snapshot's dictionary (live words are 0..599;
    /// throws IndexOutOfRangeException beyond the adopted regCount — callers
    /// guard per INV-31).</summary>
    public ushort GetRegister(ushort addr) => Words[addr];

    /// <summary>CDAB-decoded floats of a zone (spec-default bounds). Decode happens OUTSIDE the mutex.</summary>
    public float[] GetZoneFloats(ZoneKind kind)
    {
        var desc = ZoneDescriptor.DefaultFor(kind);
        return GetZoneFloats(desc.BaseReg, desc.RegCount);
    }

    /// <summary>
    /// CDAB-decoded floats for an EXPLICIT register range (ace_full reserve
    /// 600..10000 on layout v5 — DEFERRED, reads as never-written; any future
    /// zone). Bounds-guarded: only floats whose words exist in the snapshot are
    /// decoded — a layout-v2 file (650 regs) yields an EMPTY array for
    /// 600..10000. Decode happens OUTSIDE the mutex.
    /// </summary>
    public float[] GetZoneFloats(int baseReg, int regCount)
    {
        int available = Math.Max(0, Math.Min(regCount, Words.Length - baseReg));
        int count = available / RegisterMapV2.WordsPerFloat;
        var floats = new float[count];
        for (int i = 0; i < count; i++)
        {
            int baseAddr = baseReg + i * RegisterMapV2.WordsPerFloat;
            floats[i] = CdabCodec.Combine(Words[baseAddr], Words[baseAddr + 1]);
        }
        return floats;
    }
}

/// <summary>
/// THE shared bus read surface for Modbus/Web/OPC/CLI layers (W3/W5/W6).
/// BCL-only, standalone, no polling loop — callers call Snapshot() themselves.
///
/// Opens the PERSISTED file-backed mapping (default C:\ProgramData\Halliburton\
/// iCem\DataMonitoring\Data.dat, map Global\iCemDataMonitorBus_v1, mutex Global\iCemDataMonitorBusMutex_v1).
/// Must work when the file does NOT exist (clean fail: TryOpen ⇒ false + error —
/// the CLI exit-code-1 path, spec AC8) and when producers are dead (stale-hold:
/// the file keeps the last written state; per-zone age via BusZoneInfo.AgeMs).
///
/// PRODUCER-OWNED BUS (INV-39, owner decree 2026-08-27): the reader NEVER
/// creates the file, NEVER migrates layouts (migration is AddIn-owned at
/// BusWriter.Open), NEVER writes a byte — open is strictly READ-ONLY
/// (BusMapping.OpenReadOnly: FileAccess.Read + Read views). Legacy layouts
/// (v2/v3/v4) fail with an honest producer-migration error, the file untouched.
///
/// FILE-ABSENT DEGRADE (2026-08-28, rows #11/#12): a 1 Hz File.Exists probe in
/// Snapshot() (while open) closes the mapping+mutex when Data.dat is deleted
/// mid-life — reading ORPHANED file-backed pages forever is forbidden; the
/// last-good copy stale-holds (surfaced Stale=true) and the regular re-open
/// probe adopts the NEW file when the producer recreates it — no restart.
/// </summary>
/// <remarks>
/// <b>BUS-LIFECYCLE TRUTH TABLE</b> — normative reader law for the open/snapshot
/// surface (D1 = Data.dat file, D2 = named section Global\iCemDataMonitorBus_v1;
/// the host is ALWAYS running; producer = AddIn BusWriter). Pinned by
/// BusOpenTruthTableTests.
/// <para/>
/// #1  D1 absent, D2 absent, producer stopped ⇒ DEGRADED-LIVE: TryOpen=false +
///     honest «Bus file not found… (producer AddIn has not created it yet)»,
///     logged once (dedup); layers stale-hold/zeros; /api/status honest; the
///     re-open probe runs EVERY tick; NO blocking-wait anywhere.
/// #2  D1 absent→present, producer starts ⇒ TRANSITION: the next Snapshot's
///     TryOpen probe succeeds ⇒ live reading WITHOUT restart; DataBusLog.Success()
///     re-arms the dedup gate.
/// #3  D1 present_valid, D2 absent, producer stopped ⇒ UNNAMED mapping
///     (SECTION-CREATION LAW, BusMapping.OpenReadOnly): the last written bytes
///     are read, zone aging is honest; when the producer starts and creates its
///     section nothing is needed reader-side — file-backed mappings of one file
///     are coherent (same pages); no transition log required.
/// #4  D1 present_valid, D2 exists_RW, producer running ⇒ NORMAL mode: attach
///     OpenExisting(Read).
/// #5  D1 present_legacy (v2–v4), any D2 ⇒ honest «producer migration required»
///     error, the file is NOT touched, degraded; the retry probe runs every
///     tick — the producer's FULL MIGRATION (AddIn-owned) expands the file ⇒
///     the next TryOpen succeeds.
/// #6  D1 present_foreign ⇒ fail-safe typed error, degraded, retry (if the
///     file is replaced with a valid one, the next probe adopts it).
/// #7  D1 present_valid, D2 exists_RO (POISONED — created by a PRE-FIX reader):
///     the fixed code never creates such sections; if one is encountered (an
///     old reader binary still alive), this reader attaches and reads the
///     file's bytes normally — but the producer stays disabled: PRODUCER-SIDE
///     REMEDIATION = restart/replace the old reader binary (deliberately NOT
///     automated — the reader code stays simple).
/// #8  D1 present_valid, D2 exists_RW smaller than the required view ⇒ typed
///     BusAccessDeniedException.ForSectionCapacity ⇒ _accessDeniedFatal —
///     ordered restart (producer first, then consumers).
/// #9  file ACL deny, any D2 ⇒ typed ForPath ⇒ fatal (sticky).
/// #10 D1 present_valid, D2 exists_RW, producer STOPPED mid-life ⇒ keep reading
///     the file (last written bytes); zones age ⇒ stale-hold (existing law).
/// #11 D1 deleted mid-life, any D2 ⇒ FILE-ABSENT DEGRADE: the cheap 1 Hz
///     File.Exists probe in Snapshot (while IsOpen) closes the mapping+mutex
///     (dispose-race law: capture locals, safe dispose), IsOpen=false, the
///     last-good copy stays (stale-hold, surfaced Stale=true), degraded; the
///     transition is logged once; when a NEW file appears the regular re-open
///     probe adopts ALREADY THE NEW file. _accessDeniedFatal is NOT reset.
///     WITHOUT this close a file-backed mapping would serve ORPHANED pages
///     forever (the section outlives the deleted name).
/// #12 D1 absent, D2 exists_RW (the section outlived the file deletion) ⇒ the
///     file-absent probe of #11 takes PRIORITY over a live-orphan attach:
///     degrade and wait for the NEW file — never read the orphan's dead bytes
///     (fresh opens refuse at the File.Exists gate too).
/// </remarks>
public sealed class BusReader : IDisposable
{
    public const string DefaultMmfPath = @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat";
    public const string DefaultMapName = BusLayout.GlobalBusName;
    public const string DefaultMutexName = @"Global\" + BusLayout.MutexName;

    private const int MutexTimeoutMs = 500;

    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    /// <summary>
    /// Open-gate (peer-reader law): serializes TryOpen/Dispose on ONE reader
    /// instance. Concurrent first callers (telemetry producer task + ring
    /// sampler share a single reader) must not race the open sequence —
    /// a lost race leaked the first MemoryMappedFile/Mutex pair.
    /// </summary>
    private readonly object _openGate = new();

    private Mutex? _mutex;
    private MemoryMappedFile? _mmf;
    private BusSnapshot? _lastGood;
    private int _regCount;
    private long _totalBytes;
    private bool _accessDeniedFatal;

    public bool IsOpen { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Last snapshot ever taken — never null after the first successful Snapshot.</summary>
    public BusSnapshot? LastSnapshot => _lastGood;

    public BusReader(string? mmfPath = null, string? mapName = null, string? mutexName = null)
    {
        _mmfPath = string.IsNullOrWhiteSpace(mmfPath) ? DefaultMmfPath : mmfPath;
        _mapName = string.IsNullOrWhiteSpace(mapName) ? DefaultMapName : mapName;
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
    }

    /// <summary>
    /// Open the persisted mapping. Clean fail (false + LastError) when the file
    /// does not exist or the layout is invalid — callers map this to exit code 1.
    /// Idempotent: repeated calls reuse the open mapping.
    ///
    /// Dynamic-regCount law (v1.3.0): the view is sized from the header
    /// regCount (64 + 2N bytes), ¬ from compile-time constants. The host
    /// VALIDATES the shape only — legacy layouts fail with the producer-
    /// migration error (INV-39); nothing is ever rewritten by a reader.
    /// </summary>
    public bool TryOpen(out string? error)
    {
        error = null;
        if (IsOpen)
            return true;

        lock (_openGate)
        {
            // Double-check: a concurrent caller may have completed the open
            // while this one waited on the gate.
            if (IsOpen)
                return true;
            return TryOpenCore(out error);
        }
    }

    private bool TryOpenCore(out string? error)
    {
        error = null;
        if (_accessDeniedFatal)
        {
            // Fail-fast: the first attempt threw BusAccessDeniedException with
            // the full remediation. Stay disabled — pollers (ring, health,
            // Modbus) must not re-throw or re-log the same error every tick.
            error = LastError;
            return false;
        }

        try
        {
            if (!File.Exists(_mmfPath))
            {
                error = $"Bus file not found: {_mmfPath} (producer AddIn has not created it yet)";
                LastError = error;
                DataBusLog.Error(error); // dedup: 1 Hz pollers log this ONCE, not per tick
                return false;
            }

            // PRODUCER-OWNED BUS (INV-39): the host VALIDATES the shape only.
            // A legacy layout (v2/v3/v4) is an upgrade source OWNED by the
            // AddIn (BusWriter.Open migrates under the bus mutex) — the reader
            // fails honestly and NEVER rewrites the file.
            var shape = BusMapping.ReadShape(_mmfPath);
            if (shape.Magic != BusLayout.Magic)
            {
                error = "bus file corrupt — magic mismatch (not a station bus file)";
                LastError = error;
                DataBusLog.Error(error);
                return false;
            }

            if (BusLayout.IsUpgradeable(shape.Version, shape.LayoutVer, shape.RegCount, shape.FileLength))
            {
                error = $"bus file layoutVer={shape.LayoutVer} is an upgrade source — producer migration " +
                        $"required: start iCem once (the AddIn expands the file to layoutVer 5); " +
                        $"the host is READ-ONLY (INV-39)";
                LastError = error;
                DataBusLog.Error(error);
                return false;
            }

            if (!BusLayout.IsValidShape(shape.Magic, shape.Version, shape.LayoutVer, shape.RegCount, shape.FileLength))
            {
                error = $"unsupported bus file shape: version={shape.Version} layoutVer={shape.LayoutVer} " +
                        $"regCount={shape.RegCount} len={shape.FileLength} — fail-safe";
                LastError = error;
                DataBusLog.Error(error);
                return false;
            }

            _regCount = shape.RegCount;
            _totalBytes = BusLayout.HeaderBytes + (long)shape.RegCount * BusLayout.BytesPerReg;

            _mmf = BusMapping.OpenReadOnly(_mmfPath, _mapName, _totalBytes);
            _mutex = new Mutex(false, _mutexName);
            IsOpen = true;
            LastError = null;
            DataBusLog.Success(); // re-arm the dedup gate — the next failure logs again
            return true;
        }
        catch (BusAccessDeniedException ex)
        {
            _accessDeniedFatal = true;
            LastError = ex.Message;
            DataBusLog.Error(ex.Message); // typed actionable evidence → ProgramData, once
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Any denial on the read-open path (file read DACL, section view)
            // becomes the typed actionable exception — once.
            var wrapped = BusAccessDeniedException.ForPath(_mmfPath, ex);
            _accessDeniedFatal = true;
            LastError = wrapped.Message;
            DataBusLog.Error(wrapped.Message);
            throw wrapped;
        }
        catch (Exception ex)
        {
            error = $"Bus open failed: {ex.Message}";
            LastError = error;
            DataBusLog.Error(error);
            return false;
        }
    }

    /// <summary>
    /// Copy the FULL header + body under the mutex (reader law, CONTRACT_BusV2 §5).
    /// Mutex timeout (500 ms) ⇒ returns the LAST-GOOD snapshot with Stale = true.
    /// File deleted mid-life while open (rows #11/#12) ⇒ closes the mapping and
    /// returns the LAST-GOOD snapshot with Stale = true (file-absent degrade).
    /// Returns null when never successfully opened.
    /// </summary>
    public BusSnapshot? Snapshot()
    {
        // FILE-ABSENT DEGRADE (BUS-LIFECYCLE rows #11/#12): while OPEN, a cheap
        // stat probe (1 Hz poll cadence — negligible) detects Data.dat vanishing
        // mid-life. A file-backed mapping keeps serving ORPHANED pages forever
        // after the name is deleted — silent dead-bus data. LAW: file-absent
        // wins over any live mapping/orphan (row #12 priority) — close and
        // stale-hold; the re-open probe below adopts the NEW file when the
        // producer recreates it.
        if (IsOpen && !File.Exists(_mmfPath))
        {
            CloseForFileGone();
            return _lastGood is { } last
                ? last with { Stale = true } // degraded — honest consumer surface
                : null;
        }

        if (!IsOpen && !TryOpen(out _))
            return _lastGood; // clean fail — no data, stale-hold of whatever we had

        // Dispose-race law: capture the handles ONCE into locals. A concurrent
        // Dispose may null/dispose the fields at any point after this — every
        // access below goes through the captured locals, so a mid-flight
        // dispose degrades to the last-good snapshot instead of throwing
        // ObjectDisposedException/NullReferenceException out of a poll loop.
        var mutex = _mutex;
        var mmf = _mmf;
        if (mutex is null || mmf is null)
            return _lastGood;

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(MutexTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // previous owner died mid-write — we may proceed
        }
        catch (ObjectDisposedException)
        {
            return _lastGood; // disposed between the capture and the wait — stale-hold
        }

        if (!acquired)
        {
            return _lastGood is { } last
                ? last with { Stale = true }
                : null;
        }

        try
        {
            var snap = CopyAndParse(mmf);
            _lastGood = snap;
            return snap;
        }
        catch
        {
            return _lastGood;
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ObjectDisposedException)
            {
                // Disposed mid-copy — nothing to release.
            }
            catch (ApplicationException)
            {
                // Current thread no longer owns the mutex (dispose/adoption edge).
            }
        }
    }

    /// <summary>
    /// FILE-ABSENT DEGRADE (BUS-LIFECYCLE rows #11/#12): the backing file was
    /// deleted mid-life while the reader held a live mapping — file-backed data
    /// pages survive the name deletion, so without this close the reader would
    /// read ORPHANED bytes forever. Closes the mapping + mutex exactly like
    /// Dispose (handle swap under the open gate, dispose outside it): an
    /// in-flight Snapshot keeps operating on its captured locals and degrades
    /// per the dispose-race law. _lastGood and _accessDeniedFatal are NOT
    /// touched: the last-good copy stays as the stale-hold surface, and a fatal
    /// access-denied (rows #8/#9) stays fatal. The transition is recorded ONCE
    /// (DataBusLog dedup; the next successful open re-arms the gate). A NEW
    /// file re-opens automatically via the regular TryOpen probe — no restart.
    /// </summary>
    private void CloseForFileGone()
    {
        var line = $"bus file vanished mid-life: {_mmfPath} — live mapping closed " +
                   "(orphaned-page reads stopped); degraded stale-hold — a new file " +
                   "re-opens automatically (BUS-LIFECYCLE rows #11/#12)";

        MemoryMappedFile? mmf = null;
        Mutex? mutex = null;
        lock (_openGate)
        {
            if (IsOpen)
            {
                IsOpen = false;
                LastError = line;
                mmf = _mmf;
                _mmf = null;
                mutex = _mutex;
                _mutex = null;
            }
        }

        if (mmf is null)
            return; // lost the race — another thread already closed/disposed

        mmf.Dispose();
        mutex?.Dispose();
        DataBusLog.Error(line); // dedup gate — the transition is recorded ONCE
    }

    /// <summary>CDAB-decoded floats of a zone from the LAST snapshot (or null when no snapshot yet).</summary>
    public float[]? GetZoneFloats(ZoneKind kind) => _lastGood?.GetZoneFloats(kind);

    /// <summary>
    /// CDAB-decoded floats of an explicit register range from the LAST snapshot
    /// (or null when no snapshot yet). Bounds-guarded per snapshot dictionary:
    /// a v2-size file (650 words) yields an empty array for ace_full; the full
    /// dictionary (10001) decodes the whole declared range.
    /// </summary>
    public float[]? GetZoneFloats(int baseReg, int regCount) => _lastGood?.GetZoneFloats(baseReg, regCount);

    /// <summary>Raw register word from the LAST snapshot.</summary>
    public ushort GetRegister(ushort addr) => _lastGood?.Words[addr] ?? 0;

    private BusSnapshot CopyAndParse(MemoryMappedFile mmf)
    {
        using var acc = mmf.CreateViewAccessor(0, _totalBytes, MemoryMappedFileAccess.Read);
        var buf = new byte[_totalBytes];
        acc.ReadArray(0, buf, 0, buf.Length);

        // ── header: little-endian ──
        uint magic = BitConverter.ToUInt32(buf, BusLayout.HeaderMagicOffset);
        ushort version = BitConverter.ToUInt16(buf, BusLayout.HeaderVersionOffset);
        ushort layoutVer = BitConverter.ToUInt16(buf, BusLayout.HeaderLayoutVersionOffset);
        ushort regCount = BitConverter.ToUInt16(buf, BusLayout.HeaderRegCountOffset);

        var com = ParseZone(ZoneKind.ComMain, buf);
        var lan = ParseZone(ZoneKind.Lan, buf);
        var raw = ParseZone(ZoneKind.RawReserve, buf);
        var wits = ParseZone(ZoneKind.Wits, buf);

        // ── body: big-endian words (dictionary size = header regCount) ──
        var words = new ushort[_regCount];
        for (int i = 0; i < _regCount; i++)
        {
            int off = BusLayout.BodyOffset + i * 2;
            words[i] = (ushort)((buf[off] << 8) | buf[off + 1]);
        }

        return new BusSnapshot(
            magic, version, layoutVer, regCount, words,
            com, lan, raw, wits,
            Stale: false,
            CapturedAtTickMs: Environment.TickCount64);
    }

    private static BusZoneInfo ParseZone(ZoneKind kind, byte[] buf)
    {
        long tick = (long)BitConverter.ToUInt64(buf, BusLayout.ZoneLastWriteTickOffset(kind));
        bool active = buf[BusLayout.ZoneActiveFlagOffset(kind)] == 1;
        // LanExt shares the Lan counter (shared-liveness law, layout v3).
        uint counter = BitConverter.ToUInt32(buf, BusLayout.ZoneWriteCountOffset(kind));

        return new BusZoneInfo(kind, tick, active, counter);
    }

    public void Dispose()
    {
        IsOpen = false;

        // Swap the handles out under the gate BEFORE disposing: an in-flight
        // Snapshot keeps operating on its captured locals (stale-hold), and a
        // subsequent TryOpen may legally re-open (repeat-open law).
        MemoryMappedFile? mmf;
        Mutex? mutex;
        lock (_openGate)
        {
            mmf = _mmf;
            _mmf = null;
            mutex = _mutex;
            _mutex = null;
        }

        mmf?.Dispose();
        mutex?.Dispose();
    }
}

