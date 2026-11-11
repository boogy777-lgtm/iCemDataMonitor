using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Persisted shared-memory bus writer (net48 BCL only, TZ_ARCH_V2 §4).
    /// One writer per surface: this AddIn owns header state regs 0..3 +
    /// com_stream (4..199) + lan_stream_crio (200..399) + raw_com_line (400..599).
    /// Regs 600+ (ace_full RESERVE, DEFERRED) are NEVER written by anyone in v5.
    /// Header liveness (FILE-HEADER bytes 0..63 — zero collision with data
    /// cells, body starts at file offset 64): com block (10/18/37) ∧ raw block
    /// (28/36/45) ∧ lan block (19/27/41 — D4: governs lan_stream_crio) = ALL
    /// AddIn-owned. The future ace_full writer claims the former Wits block
    /// (49..63) via a layoutVer bump — not now.
    /// FAIL-SAFE: every public entry is swallow-all; on fatal init error the
    /// writer disables itself (IsOk=false) and the module keeps running without it.
    /// NEVER throws to vendor threads.
    /// OPEN ALGORITHM (v5 shape law — FULL MIGRATION, user decision 2026-08-17,
    /// under the named mutex, first-come-upgrades):
    ///   1. no file → create v5-full (10001 regs / 20066 B — D3);
    ///   2. IsUpgradeable (exact v2 650/1364 ∨ v3-small 1650/3364 ∨ v3-full
    ///      8404/16872 ∨ v4-full 8404/16872) → expand in place to v5-full
    ///      (layoutVer=5, regCount=10001, PRESERVE old body byte-exact, new tail
    ///      zero-filled by SetLength; v2 additionally zeroes 49..63 — v3/v4-full
    ///      need NO re-zeroing and the expansion must NOT touch 49..63);
    ///   3. IsValidShape (FINAL shape: layoutVer==5 ONLY, dynamic regCount) →
    ///      open as-is;
    ///   4. anything else (incl. v1 600/1264, arbitrary legacy regCounts) →
    ///      fail-safe (disabled writer).
    /// INC-03 ORDERED-RESTART: the named section capacity is frozen until ALL
    /// handles close — if the sized view is blocked by a stale smaller section
    /// (Windows ACCESS DENIED beyond the section capacity), the writer fails
    /// fast with BusAccessDeniedException.ForSectionCapacity carrying the
    /// ordered-restart runbook (stop consumers; producer iCem.exe FIRST, POC
    /// after) — surfaced via LastOpenError on the disabled writer.
/// Capacity is LENGTH-DERIVED: the section is mapped at the ACTUAL file
/// length (header self-description — shape law v5); the AddIn never
/// writes beyond reg 599, so any valid grown bus is transparent.
/// OPERATOR-CLASS DACL (creator-births-correct-file): every successful open
/// ensures BUILTIN\Users → Modify on the backing file (and an inherited grant
/// on its directory) BEFORE the named section is mapped — an elevated creator
/// must NOT pass its ambient ACL to the bus, or non-elevated consumers are
/// denied ReadWrite views forever. Best-effort; see InstallUsersModifyDacl.
/// STALE-HOLD: no write path ever zeroes body registers on channel loss;
/// last values persist (A4/A5); activeFlag flips are header-only.
/// INC-12 LIVENESS LAW (2026-08-29 amendment — Э6_failures.csv): com/lan zone
/// header blocks refresh ONLY on a FRESH pair write (raw bits differ from the
/// last pair at that address, state seeded from the file so writer re-opens
/// cannot fake it). The vendor tap replaying last-known frozen values keeps
/// the data bytes current but stops refreshing tick/flag/counter — the host's
/// 5 s age gate then honestly reports the zone inactive. raw_com_line keeps
/// line-ARRIVAL liveness (a real serial line is genuine evidence).
/// </summary>
    public sealed class BusWriter : IDisposable
    {
        private const int MutexWaitMs = 100;

        /// <summary>Test hook: when non-null, Open() uses this backing file path (production = null).</summary>
        internal static string BackingPathOverride;

        /// <summary>Test hook: unique mapping SECTION name (production = null → Global\iCemDataMonitorBus_v1).</summary>
        internal static string BusNameOverride;

        /// <summary>Test hook: unique mutex name (production = null → Global\iCemDataMonitorBusMutex_v1).</summary>
        internal static string MutexNameOverride;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Mutex _mutex;
        private readonly bool _ok;
        private readonly Exception _openError;   // typed fail-fast reason when the writer is disabled at open
        private long _lockTimeouts;
        private long _faultCount;
        private int _faulted;          // set when a write fails mid-run ⇒ IsOk false ⇒ module re-opens
        private bool _disposed;

        // INC-12 liveness law (2026-08-29 amendment): the com/lan zone header
        // blocks (tick/flag/counter) are refreshed ONLY by a FRESH pair write —
        // a pair whose raw bits differ from the last-written pair at that
        // address. The vendor variable tap keeps delivering last-known frozen
        // values while the acquisition is silent; rewriting them unchanged must
        // NOT fake liveness (host 5 s age gate then honestly reports inactive).
        // Data bytes are ALWAYS written (recording continuity — stale-hold
        // unchanged). State is seeded FROM THE FILE on first use under the
        // mutex, so a mid-run writer re-open (module retry loop) cannot fake
        // liveness either. Raw zone keeps line-ARRIVAL semantics (a real serial
        // line arrival is genuine evidence even when the text repeats).
        private const int ComPairSlots = BusLayout.ComStreamRegCount / 2;   // 98 pairs (4..199 even)
        private const int LanPairSlots = BusLayout.LanStreamCrioRegCount / 2; // 100 pairs (200..399 even)
        private readonly ushort[] _lastComWords = new ushort[ComPairSlots * 2];
        private readonly ushort[] _lastLanWords = new ushort[LanPairSlots * 2];
        private bool _pairStateSeeded;   // lazily seeded from the file under the mutex

        /// <summary>Last FRESH liveness mark per zone (DateTime ticks, ZoneKind
        /// positional; 0 = never). Updated ONLY inside UpdateZoneHeaderLocked —
        /// i.e. only by fresh evidence (changed pair / raw line arrival). The
        /// module staleness FSM consumes this instead of re-detecting change
        /// itself (ONE detector — replication is where bugs hatch).</summary>
        private readonly long[] _lastFreshUtcTicks = new long[8];

        private BusWriter(MemoryMappedFile mmf, MemoryMappedViewAccessor view, Mutex mutex, bool ok, Exception openError = null)
        {
            _mmf = mmf;
            _view = view;
            _mutex = mutex;
            _ok = ok;
            _openError = openError;
        }

        public bool IsOk
        {
            get { return _ok && !_disposed && Volatile.Read(ref _faulted) == 0; }
        }

        /// <summary>Typed open-failure reason (null when the writer opened OK).
        /// A BusAccessDeniedException here = INC-03 capacity-skew: the on-disk
        /// file is upgraded but a stale smaller section blocks the sized view —
        /// ordered-restart runbook applies.</summary>
        public Exception LastOpenError
        {
            get { return _openError; }
        }

        public long FaultCount
        {
            get { return Interlocked.Read(ref _faultCount); }
        }

        public long LockTimeouts
        {
            get { return Interlocked.Read(ref _lockTimeouts); }
        }

        /// <summary>
        /// Open (or create) the persisted mapping, run the v5 open algorithm, and
        /// return a writer — or a disabled writer on any failure (never throws).
        /// Sharing law: the backing file is opened with FileAccess.ReadWrite +
        /// FileShare.ReadWrite so concurrent readers (iCemDataMonitor.Tray BusReader) and a
        /// previously-mapped dying iCem never cause a sharing violation.
        /// </summary>
        public static BusWriter Open()
        {
            return OpenCore(true);
        }

        /// <summary>Quiet variant for the module retry loop (failure logging throttled by caller).</summary>
        internal static BusWriter OpenQuiet()
        {
            return OpenCore(false);
        }

        private static BusWriter OpenCore(bool logFailures)
        {
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor view = null;
            Mutex mutex = null;
            FileStream fs = null;
            bool streamOwnedByMmf = false;
            try
            {
                string backingPath = BackingPathOverride ?? BusLayout.BackingFilePath;
                string mapName = BusNameOverride ?? BusLayout.BusName;
                string mutexName = MutexNameOverride ?? BusLayout.MutexName;
                string dir = Path.GetDirectoryName(backingPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    InstallUsersModifyInheritedDacl(dir, logFailures);
                }

                // Shape decision + upgrade run ONCE, under the named mutex
                // (first-come-upgrades, CONTRACT_BusV2 §8).
                bool createdNew;
                mutex = new Mutex(false, mutexName, out createdNew);
                if (!TryEnterOpen(mutex))
                {
                    if (logFailures)
                    {
                        TapLog.Log("BusWriter: mutex busy at open — writer disabled (retry later)");
                    }
                    mutex.Close();
                    mutex = null;
                    return new BusWriter(null, null, null, false);
                }

                long mapCapacity;
                try
                {
                    mapCapacity = PrepareBackingFileLocked(backingPath, logFailures);
                }
                finally
                {
                    try { mutex.ReleaseMutex(); }
                    catch { }
                }
                if (mapCapacity <= 0)
                {
                    mutex.Close();
                    mutex = null;
                    return new BusWriter(null, null, null, false);
                }

                // OPERATOR-CLASS DACL (creator-births-correct-file): whatever path
                // produced the backing file above (fresh create, in-place upgrade,
                // valid-shape open), ensure BUILTIN\Users → Modify BEFORE the named
                // section is mapped — the section inherits the FILE's DACL at
                // CreateFromFile time, so an elevated creator must NOT pass its
                // ambient ACL to the bus (production INC: an elevated AddIn created
                // Data.dat with Users=ReadAndExecute ⇒ every non-elevated consumer
                // was denied ReadWrite views forever). Best-effort by the FAIL-SAFE
                // law: a process lacking WRITE_DAC skips the repair silently —
                // matching-elevation consumers are unaffected and the first
                // privileged open repairs the file for everyone.
                InstallUsersModifyDacl(backingPath, logFailures);

                // DEFECT-1 FIX: map via an explicitly-shared stream (net48 path-based
                // CreateFromFile opens with FileShare.Read ⇒ sharing violation vs
                // concurrent ReadWrite holders). leaveOpen:false ⇒ mmf owns the stream.
                // mapCapacity = ACTUAL file length for the validated shape — the VIEW
                // spans the whole file (length-derived; the AddIn never writes beyond
                // reg 599, so any valid grown bus is transparent).
                fs = new FileStream(backingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                try
                {
                    mmf = MemoryMappedFile.CreateFromFile(
                        fs, mapName, mapCapacity,
                        MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                    streamOwnedByMmf = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // named section already exists (created by another process) —
                    // fall back to attaching the existing section explicitly. Note:
                    // Windows reports ACCESS DENIED when the requested capacity is
                    // LARGER than the live section's frozen capacity (INC-03), so
                    // UnauthorizedAccessException is part of the normal attach path;
                    // the view-size check below discriminates the blocked case.
                    if (fs != null)
                    {
                        try { fs.Dispose(); }
                        catch { }
                        fs = null;
                    }
                    mmf = MemoryMappedFile.OpenExisting(mapName);
                }

                try
                {
                    view = mmf.CreateViewAccessor(0, mapCapacity);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    // INC-03 CAPACITY-SKEW (fail fast, typed): the on-disk file is
                    // the right size (upgraded under the mutex above), but the live
                    // named section was created by an older-dictionary producer at
                    // a smaller capacity and keeps it until EVERY handle closes.
                    // Windows reports ACCESS DENIED for views beyond the section
                    // capacity (UnauthorizedAccessException) — .NET may also raise
                    // ArgumentException on the size pre-check. Either way the cause
                    // is the frozen section capacity. Remediation = ordered restart
                    // (producer FIRST).
                    BusAccessDeniedException blocked = BusAccessDeniedException.ForSectionCapacity(backingPath, mapCapacity, ex);
                    if (logFailures)
                    {
                        TapLog.Log("BusWriter: " + blocked.Message.Replace(Environment.NewLine, " | "));
                    }
                    if (mmf != null)
                    {
                        try { mmf.Dispose(); }
                        catch { }
                    }
                    mmf = null;
                    if (fs != null && !streamOwnedByMmf)
                    {
                        try { fs.Dispose(); }
                        catch { }
                    }
                    fs = null;
                    mutex.Close();
                    mutex = null;
                    return new BusWriter(null, null, null, false, blocked);
                }

                // validate magic + version + layoutVer + regCount + length —
                // length-derived acceptance (dynamic-regCount law)
                uint magic = view.ReadUInt32(BusLayout.HeaderMagicOffset);
                ushort version = view.ReadUInt16(BusLayout.HeaderVersionOffset);
                ushort layoutVer = view.ReadUInt16(BusLayout.HeaderLayoutVersionOffset);
                ushort regCount = view.ReadUInt16(BusLayout.HeaderRegCountOffset);
                if (magic != BusLayout.Magic || !BusLayout.IsValidShape(version, layoutVer, regCount, mapCapacity))
                {
                    if (logFailures)
                    {
                        TapLog.Log(string.Format(
                            "BusWriter: header mismatch magic={0:X8} ver={1} layout={2} regs={3} len={4} — writer disabled",
                            magic, version, layoutVer, regCount, mapCapacity));
                    }
                    view.Dispose();
                    view = null;
                    mmf.Dispose();
                    mmf = null;
                    if (fs != null && !streamOwnedByMmf)
                    {
                        try { fs.Dispose(); }
                        catch { }
                    }
                    fs = null;
                    mutex.Close();
                    mutex = null;
                    return new BusWriter(null, null, null, false);
                }

                return new BusWriter(mmf, view, mutex, true);
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    TapLog.Log("BusWriter: open failed — " + ex.GetType().Name + ": " + ex.Message + " — writer disabled");
                }
                if (view != null)
                {
                    try { view.Dispose(); }
                    catch { }
                }
                if (mmf != null)
                {
                    try { mmf.Dispose(); }
                    catch { }
                }
                if (fs != null && !streamOwnedByMmf)
                {
                    try { fs.Dispose(); }
                    catch { }
                }
                if (mutex != null)
                {
                    try { mutex.Close(); }
                    catch { }
                }
                return new BusWriter(null, null, null, false);
            }
        }

        /// <summary>
        /// Under the mutex: create a fresh v5-full file, expand a known legacy
        /// shape in place (FULL MIGRATION: v2/v3-small/v3-full/v4-full → 10001
        /// regs / 20066 B), or validate the FINAL shape (layoutVer 5 only,
        /// dynamic regCount). Returns the mapping capacity (= file length) on
        /// success, 0 on any foreign shape.
        /// </summary>
        private static long PrepareBackingFileLocked(string backingPath, bool logFailures)
        {
            if (!File.Exists(backingPath))
            {
                using (FileStream create = new FileStream(backingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    create.SetLength(BusLayout.TotalFileBytes);
                    WriteHeaderToStream(create);
                }
                return BusLayout.TotalFileBytes;
            }

            ushort version, layoutVer, regCount;
            long length;
            bool readable = ReadHeaderFields(backingPath, out version, out layoutVer, out regCount, out length);

            if (readable && BusLayout.IsUpgradeable(version, layoutVer, regCount, length))
            {
                UpgradeToV5FullInPlace(backingPath, layoutVer == BusLayout.LayoutVersionV2);
                if (logFailures)
                {
                    TapLog.Log("BusWriter: file expanded in place (FULL MIGRATION) → layoutVer 5, 10001 regs, 20066 B (body preserved)");
                }
                return BusLayout.TotalFileBytes;
            }

            bool validShape = readable && BusLayout.IsValidShape(version, layoutVer, regCount, length);
            if (!validShape)
            {
                if (logFailures)
                {
                    TapLog.Log(string.Format(
                        "BusWriter: foreign file shape ver={0} layout={1} regs={2} len={3} — writer disabled",
                        version, layoutVer, regCount, length));
                }
                return 0;
            }
            return length; // ACTUAL file length — the view spans the whole file
        }

        /// <summary>
        /// Operator-class DACL on the backing FILE: BUILTIN\Users → Modify — the
        /// minimal right that allows ReadWrite views for every process in the
        /// operator class regardless of which security context created the file.
        /// Mirrors server-dotnet BusZoneWriter.InstallUsersModifyDacl: both
        /// creators of Data.dat must enforce the SAME birth contract. Best-effort
        /// by the FAIL-SAFE law (never throws to vendor threads): any ACL error
        /// is swallowed and logged once per open attempt.
        /// </summary>
        private static void InstallUsersModifyDacl(string path, bool logFailures)
        {
            try
            {
                FileSecurity security = File.GetAccessControl(path);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.Modify,
                    AccessControlType.Allow));
                File.SetAccessControl(path, security);
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    TapLog.Log("BusWriter: BUILTIN\\Users→Modify DACL not installed on " +
                               Path.GetFileName(path) + " (" + ex.Message + ") — continuing");
                }
            }
        }

        /// <summary>
        /// Operator-class DACL on the backing DIRECTORY with inheritance to files
        /// and subdirectories: any future file created here by ANY tool inherits
        /// BUILTIN\Users → Modify, so an out-of-band creator can no longer poison
        /// the bus (defense-in-depth behind InstallUsersModifyDacl). Best-effort,
        /// same swallow law.
        /// </summary>
        private static void InstallUsersModifyInheritedDacl(string dir, bool logFailures)
        {
            try
            {
                DirectorySecurity security = Directory.GetAccessControl(dir);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                Directory.SetAccessControl(dir, security);
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    TapLog.Log("BusWriter: BUILTIN\\Users→Modify inherited DACL not installed on " +
                               dir + " (" + ex.Message + ") — continuing");
                }
            }
        }

        /// <summary>
        /// FULL MIGRATION expansion (CONTRACT_BusV2 §8 + user decision 2026-08-17),
        /// called under the mutex: 1. expand file to 20066 B (SetLength zero-fills
        /// the new region); 2. rewrite identity header: layoutVer=5, regCount=10001;
        /// 3. v2 sources ONLY: zero former-wits header bytes 49..63 (reserved-zero
        /// law; v3-small ∧ v3-full ∧ v4-full already carry them zero by induction —
        /// the expansion must NOT touch them there); 4. PRESERVE body 64..(old
        /// length) verbatim (SetLength never clears existing bytes) and zone header
        /// fields 10..48.
        /// </summary>
        private static void UpgradeToV5FullInPlace(string path, bool zeroFormerWitsBlock)
        {
            using (FileStream s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                s.SetLength(BusLayout.TotalFileBytes); // extends with zeros

                byte[] head = new byte[10];
                head[0] = (byte)'S';
                head[1] = (byte)'T';
                head[2] = (byte)'N';
                head[3] = (byte)'B';
                WriteU16(head, BusLayout.HeaderVersionOffset, BusLayout.Version);
                WriteU16(head, BusLayout.HeaderLayoutVersionOffset, BusLayout.LayoutVersion);
                WriteU16(head, BusLayout.HeaderRegCountOffset, BusLayout.RegCount);
                s.Position = 0;
                s.Write(head, 0, head.Length);

                if (zeroFormerWitsBlock)
                {
                    byte[] zeros = new byte[15]; // 49..63 reserved-zero
                    s.Position = 49;
                    s.Write(zeros, 0, zeros.Length);
                }

                s.Flush();
            }
        }

        /// <summary>Read identity header fields + file length (magic checked).</summary>
        private static bool ReadHeaderFields(string path, out ushort version, out ushort layoutVer, out ushort regCount, out long length)
        {
            version = 0;
            layoutVer = 0;
            regCount = 0;
            length = 0;
            try
            {
                using (FileStream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    length = s.Length;
                    if (length < 10)
                    {
                        return false;
                    }
                    byte[] head = new byte[10];
                    int read = 0;
                    while (read < 10)
                    {
                        int n = s.Read(head, read, 10 - read);
                        if (n <= 0)
                        {
                            return false;
                        }
                        read += n;
                    }
                    if (head[0] != (byte)'S' || head[1] != (byte)'T' || head[2] != (byte)'N' || head[3] != (byte)'B')
                    {
                        return false; // bad magic ⇒ never upgradeable, never valid
                    }
                    version = (ushort)(head[4] | (head[5] << 8));
                    layoutVer = (ushort)(head[6] | (head[7] << 8));
                    regCount = (ushort)(head[8] | (head[9] << 8));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Write a fresh v5 header (identity fields; everything else zero).</summary>
        private static void WriteHeaderToStream(Stream s)
        {
            byte[] hdr = new byte[BusLayout.HeaderBytes];
            hdr[0] = (byte)'S';
            hdr[1] = (byte)'T';
            hdr[2] = (byte)'N';
            hdr[3] = (byte)'B';
            WriteU16(hdr, BusLayout.HeaderVersionOffset, BusLayout.Version);
            WriteU16(hdr, BusLayout.HeaderLayoutVersionOffset, BusLayout.LayoutVersion);
            WriteU16(hdr, BusLayout.HeaderRegCountOffset, BusLayout.RegCount);
            s.Position = 0;
            s.Write(hdr, 0, hdr.Length);
            s.Flush();
        }

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Write one body register (int16) — big-endian bytes (Modbus wire image).</summary>
        public void WriteReg16(int regAddress, short value)
        {
            if (!IsOk)
            {
                return;
            }
            if (regAddress < 0 || regAddress > BusLayout.AddInMaxReg)
            {
                return; // v5 guard: this AddIn never writes 600+
            }
            try
            {
                if (!TryEnter())
                {
                    return;
                }
                try
                {
                    long off = BusLayout.BodyOffset + (long)regAddress * 2;
                    _view.Write(off, (byte)(((ushort)value >> 8) & 0xFF));
                    _view.Write(off + 1, (byte)((ushort)value & 0xFF));
                    _view.Flush();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
                MarkFaulted();
            }
        }

        /// <summary>
        /// Write one float32 CDAB pair — full 4 bytes: reg[N] = low word (bytes
        /// C,D), reg[N+1] = high word (bytes A,B), big-endian bytes per word —
        /// all under ONE mutex hold (per-float-pair atomicity, CONTRACT_BusV2 §5).
        /// Zone law v5/v5.1:
        ///   ComMain  → com_stream (4..199, even address) + com liveness block;
        ///   Lan      → lan_stream_crio (200..399, even address) + lan liveness
        ///               block (D4: tick@19..26, flag@27, counter@41..44 —
        ///               AddIn-owned, same pattern as com/raw). FULL 4-byte CDAB
        ///               (INC-06 law — no byte-width truncation);
        ///   RawReserve → raw_com_line pairs are written ONLY by the WriteRawLine
        ///               batch (v5.1) — ad-hoc pair writes here are a no-op guard;
        ///   Wits/LanExt → never written (no-op).
        /// Addresses below zone floors or beyond zone tops are SILENT NO-OPS.
        /// </summary>
        public void WriteFloatPair(ZoneKind zone, int registerAddress, float value)
        {
            ushort low, high;
            CdabCodec.Split(value, out low, out high);
            WritePairWords(zone, registerAddress, low, high);
        }

        /// <summary>
        /// Write one int32 as RAW BITS into a register pair — SAME wire image as
        /// a CDAB float pair (reg[N] = low word BE, reg[N+1] = high word BE, one
        /// mutex hold) so an int32/uint32 read of the pair in the consumer's
        /// standard word order yields the value (contract v5.1, CHANGE 1 user
        /// decree 2026-08-18: the Modbus consumer owns read formatting; the
        /// float32 interpretation of the pair is garbage BY DECREE).
        /// v5.1 call site: com_stream idx0 timestamp (regs 4..5 =
        /// UnixTimestampSeconds — UnixTimestamp.cs). Same zone guards as
        /// WriteFloatPair.
        /// </summary>
        public void WriteInt32Pair(ZoneKind zone, int registerAddress, int value)
        {
            uint bits = unchecked((uint)value);
            WritePairWords(zone, registerAddress, (ushort)(bits & 0xFFFF), (ushort)(bits >> 16));
        }

        /// <summary>Pair-zone address guard (ONE copy of the zone law — both pair
        /// writers funnel through it; replication = drift vector).</summary>
        private static bool IsPairZoneAddress(ZoneKind zone, int registerAddress)
        {
            if (zone == ZoneKind.ComMain)
            {
                return registerAddress >= BusLayout.ComStreamBaseReg
                    && registerAddress + 1 <= BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount - 1;
            }
            if (zone == ZoneKind.Lan)
            {
                return registerAddress >= BusLayout.LanStreamCrioBaseReg
                    && registerAddress + 1 <= BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount - 1;
            }
            return false; // RawReserve (batch-only via WriteRawLine)/Wits/LanExt: not ad-hoc pair zones
        }

        /// <summary>Core pair write: 4 bytes (low word BE, high word BE) under
        /// ONE mutex hold. Guarded addresses are silent no-ops. INC-12 liveness
        /// law: the zone header block (tick/flag/counter) is refreshed ONLY
        /// when this pair CHANGED vs the last-written pair at that address
        /// (file-seeded — a frozen vendor rewrite never fakes liveness); the
        /// data bytes land unconditionally (recording continuity).</summary>
        internal void WritePreparedPair(PreparedPairWrite request)
        {
            WritePairWords(request.Zone, request.Address, request.Low, request.High);
        }

        private void WritePairWords(ZoneKind zone, int registerAddress, ushort low, ushort high)
        {
            if (!IsOk)
            {
                return;
            }
            if (!IsPairZoneAddress(zone, registerAddress))
            {
                return;
            }
            try
            {
                if (!TryEnter())
                {
                    return;
                }
                try
                {
                    SeedPairStateLocked();

                    long off = BusLayout.BodyOffset + (long)registerAddress * 2;
                    _view.Write(off, (byte)((low >> 8) & 0xFF));
                    _view.Write(off + 1, (byte)(low & 0xFF));
                    _view.Write(off + 2, (byte)((high >> 8) & 0xFF));
                    _view.Write(off + 3, (byte)(high & 0xFF));

                    // INC-12 fresh-frame law: header liveness ONLY on a real
                    // value change (raw-bits compare — identical for the float
                    // and int32 pair paths, ONE funnel).
                    if (TrackPairWords(zone, registerAddress, low, high))
                    {
                        UpdateZoneHeaderLocked(zone); // D4: com ∧ lan ∧ (raw via WriteRawLine) liveness = AddIn-owned
                    }
                    _view.Flush();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
                MarkFaulted();
            }
        }

        /// <summary>One-time per-writer seed of the pair-change state FROM THE
        /// FILE (under the mutex): a writer re-opened mid-run must not treat
        /// frozen file values as fresh (INC-12). Zero pairs are seeded as
        /// "last = zero" — an unchanged 0.0 rewrite after re-open is then
        /// correctly non-fresh (a real zero steady-state behaves the same).</summary>
        private void SeedPairStateLocked()
        {
            if (_pairStateSeeded)
            {
                return;
            }
            _pairStateSeeded = true;
            SeedZoneWords(BusLayout.ComStreamBaseReg, ComPairSlots, _lastComWords);
            SeedZoneWords(BusLayout.LanStreamCrioBaseReg, LanPairSlots, _lastLanWords);
        }

        private void SeedZoneWords(int baseReg, int slots, ushort[] words)
        {
            for (int slot = 0; slot < slots; slot++)
            {
                long off = BusLayout.BodyOffset + (long)(baseReg + slot * 2) * 2;
                // The bus stores each word BIG-endian; _view.ReadUInt16 reads
                // LITTLE-endian ⇒ byteswap gives back the logical word that
                // WritePairWords compared against.
                ushort leLow = _view.ReadUInt16(off);
                ushort leHigh = _view.ReadUInt16(off + 2);
                words[slot * 2] = (ushort)((leLow >> 8) | (leLow << 8));
                words[slot * 2 + 1] = (ushort)((leHigh >> 8) | (leHigh << 8));
            }
        }

        /// <summary>Update the per-address last-written word pair; true ⇔ the
        /// new words differ (a FRESH value) — false ⇔ frozen rewrite.</summary>
        private bool TrackPairWords(ZoneKind zone, int registerAddress, ushort low, ushort high)
        {
            if (zone == ZoneKind.ComMain)
            {
                return TrackWords(_lastComWords, (registerAddress - BusLayout.ComStreamBaseReg) / 2, low, high);
            }
            if (zone == ZoneKind.Lan)
            {
                return TrackWords(_lastLanWords, (registerAddress - BusLayout.LanStreamCrioBaseReg) / 2, low, high);
            }
            return true; // unreachable (IsPairZoneAddress guards) — safe default: refresh
        }

        private static bool TrackWords(ushort[] words, int slot, ushort low, ushort high)
        {
            int i = slot * 2;
            bool changed = words[i] != low || words[i + 1] != high;
            words[i] = low;
            words[i + 1] = high;
            return changed;
        }

        /// <summary>
        /// Write the last received COM line into raw_com_line (400..599) —
        /// contract v5.1 (CHANGE 2 user decree 2026-08-18): the line is split
        /// into CSV fields; field i (0-based, POSITIONAL over the raw line
        /// AS-IS — excludeFromTelemetry fields INCLUDED, DA decision) parses to
        /// a double and lands as a float32 CDAB pair at 400+i*2; up to 100
        /// fields (RawCsvFields law). The retired v5 u16-per-char path is GONE
        /// (git history only — no dual-write).
        /// ONE mutex hold for the whole line (per-line atomicity) + ONE raw
        /// liveness update (tick/activeFlag/counter) at the end — the liveness
        /// mark is line-ARRIVAL evidence and fires even when zero fields parse
        /// (Э2b row C06).
        /// STALE-HOLD: an unparseable field writes NOTHING (last good pair
        /// persists); tail pairs beyond a shorter line persist (never zeroed);
        /// an empty line is a silent no-op.
        /// </summary>
        public void WriteRawLine(string line)
        {
            if (!IsOk || string.IsNullOrEmpty(line))
            {
                return;
            }
            var fields = RawCsvFields.Resolve(line);
            try
            {
                if (!TryEnter())
                {
                    return;
                }
                try
                {
                    foreach (RawCsvFields.Field field in fields)
                    {
                        ushort low, high;
                        CdabCodec.Split((float)field.Value, out low, out high);
                        long off = BusLayout.BodyOffset + (long)(BusLayout.RawComLineBaseReg + field.Index * 2) * 2;
                        _view.Write(off, (byte)((low >> 8) & 0xFF));
                        _view.Write(off + 1, (byte)(low & 0xFF));
                        _view.Write(off + 2, (byte)((high >> 8) & 0xFF));
                        _view.Write(off + 3, (byte)(high & 0xFF));
                    }
                    UpdateZoneHeaderLocked(ZoneKind.RawReserve);
                    _view.Flush();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
                MarkFaulted();
            }
        }

        /// <summary>Set zone activeFlag (0/1) — A4/A5 stale-hold semantics.
        /// v5 D4: ComMain ∧ Lan ∧ RawReserve flags are ALL AddIn-owned (com_stream,
        /// lan_stream_crio, raw_com_line liveness); flag 0 = hold (channel loss —
        /// data cells NEVER zeroed); Wits/LanExt are silent no-ops.</summary>
        public void SetZoneActive(ZoneKind zone, bool active)
        {
            if (!IsOk)
            {
                return;
            }
            if (zone != ZoneKind.ComMain && zone != ZoneKind.Lan && zone != ZoneKind.RawReserve)
            {
                return; // wits/lan_ext dead in v5
            }
            try
            {
                if (!TryEnter())
                {
                    return;
                }
                try
                {
                    _view.Write(ZoneFlagOffset(zone), active ? (byte)1 : (byte)0);
                    _view.Flush();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
                MarkFaulted();
            }
        }

        /// <summary>
        /// Mid-run failure (view/mutex died): flip the fault flag so IsOk=false —
        /// the module retry loop re-opens the writer. Self-healing, one path.
        /// </summary>
        private void MarkFaulted()
        {
            Interlocked.Increment(ref _faultCount);
            Volatile.Write(ref _faulted, 1);
        }

        /// <summary>Refresh one zone's header liveness block (tick u64 + flag
        /// u8=1 + counter u32+1, wrapping) under the held mutex. INC-12 law
        /// (2026-08-29): called ONLY from a FRESH pair write (changed bits) or
        /// a raw line arrival — a frozen same-value rewrite never reaches here,
        /// so tick/flag/counter all mean "fresh data landed", and the host's
        /// 5 s age gate honestly reports an inactive zone when the vendor tap
        /// only replays last-known values.</summary>
        private void UpdateZoneHeaderLocked(ZoneKind zone)
        {
            long tick = DateTime.UtcNow.Ticks;
            int zi = (int)zone;
            if (zi >= 0 && zi < _lastFreshUtcTicks.Length)
            {
                Volatile.Write(ref _lastFreshUtcTicks[zi], tick); // fresh-evidence timestamp (module FSM input)
            }
            _view.Write(ZoneTickOffset(zone), tick);
            _view.Write(ZoneFlagOffset(zone), (byte)1);
            int countOffset = ZoneCountOffset(zone);
            uint count = _view.ReadUInt32(countOffset) + 1U; // wrapping u32
            _view.Write(countOffset, count);
        }

        /// <summary>UTC time of the zone's last FRESH liveness mark (changed
        /// pair / raw line arrival) — DateTime.MinValue when none yet. The
        /// module staleness FSM consumes this: a frozen-rewrite stream leaves
        /// it parked ⇒ Poll drops the zone after the idle window (INC-12).</summary>
        public DateTime LastLivenessMarkUtc(ZoneKind zone)
        {
            int zi = (int)zone;
            if (zi < 0 || zi >= _lastFreshUtcTicks.Length)
            {
                return DateTime.MinValue; // out-of-range/historical zones are never marked
            }
            long t = Volatile.Read(ref _lastFreshUtcTicks[zi]);
            return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }

        /// <summary>Zone header offset table — DATA, not control flow (decision-table
        /// collapse of the former three parallel switch-on-zone methods: 9 case labels
        /// + 3 defaults of replicated control flow → one lookup; replication = drift
        /// vector). Row order = ZoneKind positional binding (APPEND-ONLY); columns =
        /// {tick, flag, count}. Wits ∧ LanExt rows = the historical wits block
        /// (49..63 reserved-zero — never written in v5; kept for lookup totality).</summary>
        private static readonly int[,] ZoneHeaderOffsets =
        {
            // tick                                       flag                                     count
            { BusLayout.ZoneComLastWriteTickOffset,  BusLayout.ZoneComActiveFlagOffset,  BusLayout.ProducerComWriteCountOffset  }, // ComMain
            { BusLayout.ZoneLanLastWriteTickOffset,  BusLayout.ZoneLanActiveFlagOffset,  BusLayout.ProducerLanWriteCountOffset  }, // Lan (D4: lan_stream_crio)
            { BusLayout.ZoneRawLastWriteTickOffset,  BusLayout.ZoneRawActiveFlagOffset,  BusLayout.ProducerRawWriteCountOffset  }, // RawReserve
            { BusLayout.ZoneWitsLastWriteTickOffset, BusLayout.ZoneWitsActiveFlagOffset, BusLayout.ProducerWitsWriteCountOffset }, // Wits (historical)
            { BusLayout.ZoneWitsLastWriteTickOffset, BusLayout.ZoneWitsActiveFlagOffset, BusLayout.ProducerWitsWriteCountOffset }, // LanExt (historical → wits block)
        };

        /// <summary>Total lookup (the former switch-default law preserved: any
        /// out-of-range zone ⇒ the historical wits block, never a throw inside a
        /// write path).</summary>
        private static int ZoneOffset(ZoneKind zone, int column)
        {
            int i = (int)zone;
            if (i < 0 || i >= ZoneHeaderOffsets.GetLength(0))
            {
                i = (int)ZoneKind.Wits;
            }
            return ZoneHeaderOffsets[i, column];
        }

        private static int ZoneTickOffset(ZoneKind zone)
        {
            return ZoneOffset(zone, 0);
        }

        private static int ZoneFlagOffset(ZoneKind zone)
        {
            return ZoneOffset(zone, 1);
        }

        private static int ZoneCountOffset(ZoneKind zone)
        {
            return ZoneOffset(zone, 2);
        }

        /// <summary>Mutex acquisition at OPEN time (shape decision is serialized).</summary>
        private static bool TryEnterOpen(Mutex mutex)
        {
            try
            {
                bool got;
                try
                {
                    got = mutex.WaitOne(MutexWaitMs);
                }
                catch (AbandonedMutexException)
                {
                    got = true; // previous owner died; shape work is idempotent
                }
                return got;
            }
            catch
            {
                return false;
            }
        }

        private bool TryEnter()
        {
            try
            {
                bool got;
                try
                {
                    got = _mutex.WaitOne(MutexWaitMs);
                }
                catch (AbandonedMutexException)
                {
                    got = true; // previous owner died mid-write; file state is last-write-consistent per pair
                }
                if (!got)
                {
                    Interlocked.Increment(ref _lockTimeouts);
                }
                return got;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                if (_mutex != null)
                {
                    _mutex.Close();
                }
            }
            catch { }
            try
            {
                if (_view != null)
                {
                    _view.Dispose();
                }
            }
            catch { }
            try
            {
                if (_mmf != null)
                {
                    _mmf.Dispose();
                }
            }
            catch { }
        }
    }
}
