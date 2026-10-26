using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Halliburton.iCem.CementingData.Objects;
using Halliburton.iCem.CementingData.VariableSelector;
using Halliburton.iCem.RealTime.SerialDataSetup;
using Halliburton.Oz.CementingServices;
using Halliburton.Oz.CementingServices.VariableSelector;
using Burservis.iCem.DataMonitor.Bus;
using Halliburton.Oz.Presentation;
using Halliburton.Oz.RealTime.LANSetup;
using Halliburton.Oz.RealTime.SerialSetup;
using Halliburton.Oz.RealTime.VariableSelector;
using Microsoft.Practices.Composite.Modularity;

namespace Burservis.iCem.DataMonitor
{
    /// <summary>
    /// DataMonitor v5 Prism AddIn — shared-memory bus writer (bus register map v5,
    /// user decree 2026-08-17). DUMB TRANSLATOR: byte-exact copy, no transforms.
    /// Writes:
    ///   header state regs 0..3 — {comPortActive (D1 first-valid-wins latch),
    ///   caseState (RealtimeState passthrough), caseLanState (LANManager.
    ///   LANStatus passthrough), heartbeat (+1/s ALWAYS while loaded, wrap
    ///   32000→0 — D2; NOT Streaming-gated)};
    ///   com_stream 4..199 (float32 CDAB, addr=4+idx*2 — full poll, com map;
    ///   EXCEPT idx0 regs 4..5: UnixTimestampSeconds int32 RAW BITS, CDAB word
    ///   order — contract v5.1 CHANGE 1 user decree 2026-08-18, consumer owns
    ///   read formatting; idx1 Unit Num stays float32, documented asymmetry);
    ///   lan_stream_crio 200..399 (float32 CDAB, addr=200+idx*2 — 96 cRIO
    ///   signals, LAN-equipment sources only; full 4-byte CDAB, INC-06 fix;
    ///   D4: lan liveness block tick@19/flag@27/counter@41 = AddIn-owned,
    ///   marked per write batch, flag 0 on hold);
    ///   raw_com_line 400..599 (contract v5.1 CHANGE 2 user decree 2026-08-18:
    ///   last COM line from the latched device, CSV-split POSITIONAL — field i
    ///   → float32 CDAB pair at 400+i*2, ≤100 fields, unparseable ⇒ not
    ///   written/stale-hold; u16-per-char path RETIRED; D1 discard law).
    /// NEVER writes 600+ (ace_full RESERVE 600..10000 — DEFERRED, nobody
    /// writes). STALE-HOLD: no zeroing on channel loss; last values persist;
    /// heartbeat unaffected by channel state.
    /// Vendor-safety: swallow-all, zero blocking on vendor threads (bus I/O on
    /// a dedicated worker), background timers only. 24/7-safe: bounded queue
    /// (drop-oldest), bounded diagnostics, capped rotating TapLog.
    /// </summary>
    [Module(ModuleName = "DataMonitorModule")]
    [ModuleDependency("CementingServicesModule")]
    public class DataMonitorModule : IModule, IDisposable
    {
        /// <summary>EquipmentPort.LAN enum value — LAN equipment discriminator
        /// (EquipmentVariableSource.SourceComPort passthrough).</summary>
        private const int LanEquipmentPort = 17;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

        private readonly IWindow _owner;
        private Timer _hzTimer;
        private bool _disposed;
        private BusWriter _bus;

        private EventHandler<ComPortPropertyChangedEventArgs> _comPortPropertyHandler;

        // Zone liveness FSM ×3 (statechart-as-pattern: explicit TOTAL transition
        // function — replaces the replicated (flag,timestamp) field pairs and the
        // scattered if-sites; Э2b rows B02/B09/B13/B17..B20; ONE machine, THREE
        // instances — replication is where bugs hatch: fix com, forget lan)
        private readonly ZoneLiveness _comLiveness = new ZoneLiveness(ZoneKind.ComMain);
        private readonly ZoneLiveness _lanLiveness = new ZoneLiveness(ZoneKind.Lan);
        private readonly ZoneLiveness _rawLiveness = new ZoneLiveness(ZoneKind.RawReserve);
        // INC-12: last fresh-evidence timestamp SEEN from the writer per zone —
        // advancing = a fresh (changed-value) write landed; parked = frozen taps.
        private DateTime _lastSeenComFreshUtc = DateTime.MinValue;
        private DateTime _lastSeenLanFreshUtc = DateTime.MinValue;
        private DateTime _lastSeenRawFreshUtc = DateTime.MinValue;
        private ushort _heartbeat;
        private long _skippedCom;
        private long _skippedLan;
        private long _dupSkips;                   // per-tick address dedup drops (alias double-count)
        private long _badTypeSkips;               // boxed non-numeric types (DateTime/String/bool/...)
        private long _comPairs;
        private long _lanPairs;
        private long _rawLines;
        private long _latchDiscards;              // D1: lines from non-latched devices dropped
        private long _latchReleases;              // D1: re-arbitrations on stream loss
        private int _rawLogGate;
        private int _lanLogGate;
        private int _traceTicksLeft = 3;          // detailed COMTRACE for the first N poll ticks
        private readonly object _skipLock = new object();
        private readonly Dictionary<string, long> _missNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _badTypeNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastBusOpenAttemptUtc = DateTime.MinValue;
        private int _busFailAttempts;
        private bool _busEverOpened;

        // ── POLL diagnostics (stand investigation 2026-08-29): the com/lan
        //    poll path is gated on project→ActiveCase→VariableProvider→
        //    map-matched candidates while raw_com_line needs NONE of that —
        //    every gate used to be a SILENT early-return, so a stand with raw
        //    flowing and com/lan empty had zero evidence WHICH gate failed.
        //    Every skip reason is now surfaced: state-change line + ≥10 s
        //    cadence reminder + a poll= field on the 10 s HB line.
        private string _pollSkipReason;           // current skip reason (null = poll flowing)
        private DateTime _lastSkipLogUtc = DateTime.MinValue;
        private int _gatherSourceCount;           // last gather: sources in provider.VariableSources
        private int _gatherLanSourceCount;        // last gather: sources with SourceComPort==17 (EquipmentPort.LAN)
        private int _gatherVariableCount;         // last gather: variables seen across sources
        private int _gatherLanVariableCount;      // last gather: variables seen on LAN-affine sources
        private string _gatherError;              // last gather: vendor-object failure (type name), null = clean

        // ── INC-13 LAN source diagnostics (stand 2026-08-30): 10 LAN sources
        //    expose overlapping variable names; first-convertible-wins can pin
        //    an address to a FROZEN source (dead/LOST unit or non-recording
        //    equipment) while the live unit's values are counted away as dup.
        //    Per-source evidence: identity (eq/unit/rec/online), freshness
        //    (vendor CurrentTime age), resolver wins, changed-value counter —
        //    «какой источник живой, какой замороженный, кого выбрал resolver»
        //    becomes visible from TapOut.log without a debugger. ────────────
        private static readonly TimeSpan LanFreshWindow = TimeSpan.FromSeconds(10);
        private readonly Dictionary<string, LanSourceTracker> _lanTrackers =
            new Dictionary<string, LanSourceTracker>(StringComparer.OrdinalIgnoreCase);
        private List<LanSourceTracker> _lanSourceOrder = new List<LanSourceTracker>(); // gather order (last tick)
        private string _lanSourceSignature;       // change-detector for the emit trigger
        private DateTime _lanEmitLastUtc = DateTime.MinValue;
        private static readonly string LanUnitQuestionMark = "?";

        // D1 first-valid-wins latch (reg 0 source of truth; vendor-free state machine)
        private readonly ComFeedLatch _latch = new ComFeedLatch();

        public DataMonitorModule(IWindow owner)
        {
            _owner = owner;
            if (owner != null)
            {
                try
                {
                    owner.WindowModules.Add(this);
                }
                catch
                {
                    // best-effort lifecycle registration
                }
            }
        }

        public void Initialize()
        {
            try
            {
                _bus = BusWriter.Open();
                _busEverOpened = true;
                if (_bus.IsOk)
                {
                    TapLog.Log("DataMonitor v5: bus open OK (Global\\iCemDataMonitorBus_v1)");
                }
                else
                {
                    TapLog.Log("DataMonitor v5: bus DISABLED — header/file validation failed; module continues headless");
                }
            }
            catch
            {
                // never throw out of Initialize
            }
            try
            {
                // Prime lazy UI-owned singletons on the UI thread (Initialize runs on UI thread)
                if (_owner != null)
                {
                    var primeProject = _owner.ProjectManager;
                }
                var primePorts = ComPortManager.Instance.ComPorts;
                var primeLan = LANManager.Instance;
            }
            catch
            {
                // swallowed
            }
            try
            {
                _comPortPropertyHandler = OnComPortPropertyChanged;
                ComPortManager.Instance.ComPortPropertyChanged += _comPortPropertyHandler;
            }
            catch
            {
                // swallowed — raw zone stays silent
            }
            try
            {
                _hzTimer = new Timer(TimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            catch
            {
                // swallowed
            }
        }

        // ── 1 Hz tick: heartbeat (ALWAYS), header regs, latch policy, poll ─────

        private void TimerTick(object state)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                TryReopenBus();
                AdvanceHeartbeat();
                UpdateLatchLossPolicy();
                WriteHeaderRegisters();
                UpdateStaleness();
                PollStreams();
                if ((_heartbeat % 10) == 0)
                {
                    TapLog.Log(string.Format(
                        "HB {0} com={1} lan={2} case={3} pairs={4}/{5} rawLines={6} skipped={7}/{8} dup={9} badtype={10} latchDiscards={11} qdrop={12} locktimeouts={13} poll={14}",
                        _heartbeat,
                        _latch.PortNumber,
                        ResolveCaseLanState(),
                        ResolveCaseState(),
                        Interlocked.Read(ref _comPairs), Interlocked.Read(ref _lanPairs),
                        Interlocked.Read(ref _rawLines),
                        Interlocked.Read(ref _skippedCom), Interlocked.Read(ref _skippedLan),
                        Interlocked.Read(ref _dupSkips),
                        Interlocked.Read(ref _badTypeSkips),
                        Interlocked.Read(ref _latchDiscards),
                        BusWriteQueue.Dropped, _bus != null ? _bus.LockTimeouts : 0,
                        _pollSkipReason ?? "ok"));
                }
                if ((_heartbeat % 60) == 0)
                {
                    DumpSkipTop();
                    EmitLanSourceDiagnostics(); // INC-13: guaranteed 60 s LANSRC cadence
                }
            }
            catch
            {
                // never propagate from a timer callback
            }
        }

        // ── D2 heartbeat: +1/s ALWAYS while the AddIn is loaded (pipeline-alive
        //    signal), wraps 32000→0 — DECOUPLED from Streaming state ────────────

        private void AdvanceHeartbeat()
        {
            _heartbeat = (ushort)(_heartbeat + 1);
            if (_heartbeat >= BusLayout.HeartbeatWrap)
            {
                _heartbeat = 0; // D2: wrap 32000→0
            }
        }

        // ── header state regs 0..3 (bus register map v5, per-register writes
        //    under the BusWriter mutex, CURRENT-state only — no timeout logic) ──

        private void WriteHeaderRegisters()
        {
            if (_bus == null || !_bus.IsOk)
            {
                return;
            }
            BusWriter bus = _bus;
            try
            {
                bus.WriteReg16(BusLayout.RegComPort, (short)_latch.PortNumber);
                bus.WriteReg16(BusLayout.RegCaseState, ResolveCaseState());
                bus.WriteReg16(BusLayout.RegCaseLanState, ResolveCaseLanState());
                bus.WriteReg16(BusLayout.RegHeartbeat, (short)_heartbeat);
            }
            catch
            {
                // per-tick register writes must never take the timer down
            }
        }

        // ── D1 re-arbitration policy: the latch is released ONLY on stream loss
        //    (port state left {Streaming,Recording} ∨ silence beyond IdleTimeout).
        //    No zeroing of data zones — STALE-HOLD law. ─────────────────────────

        private void UpdateLatchLossPolicy()
        {
            if (!_latch.IsLatched)
            {
                return;
            }
            bool lost = false;
            try
            {
                IPort port = FindPortByEquipmentPort(_latch.PortNumber);
                lost = port == null
                    || (port.State != ComPortStates.Streaming && port.State != ComPortStates.Recording);
            }
            catch
            {
                // probe failure ⇒ keep the latch (never release on our own error)
            }
            if (!lost && _latch.IsSilentBeyond(DateTime.UtcNow, IdleTimeout))
            {
                lost = true;
            }
            if (lost)
            {
                _latch.Release();
                Interlocked.Increment(ref _latchReleases);
            }
        }

        private static IPort FindPortByEquipmentPort(int equipmentPort)
        {
            try
            {
                var ports = ComPortManager.Instance.ComPorts;
                if (ports == null)
                {
                    return null;
                }
                foreach (IPort port in ports)
                {
                    if (port != null && (int)port.EquipmentPort == equipmentPort)
                    {
                        return port;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ── bus re-open retry loop (DEFECT-2 fix: self-healing, 10 s cadence) ──

        private void TryReopenBus()
        {
            try
            {
                if (_bus != null && _bus.IsOk)
                {
                    return;
                }
                DateTime now = DateTime.UtcNow;
                if ((now - _lastBusOpenAttemptUtc).TotalSeconds < 10)
                {
                    return;
                }
                _lastBusOpenAttemptUtc = now;

                BusWriter fresh = _busEverOpened ? BusWriter.OpenQuiet() : BusWriter.Open();
                _busEverOpened = true;
                if (fresh.IsOk)
                {
                    BusWriter old = _bus;
                    _bus = fresh;
                    if (old != null)
                    {
                        try { old.Dispose(); }
                        catch { }
                    }
                    _busFailAttempts = 0;
                    TapLog.Log("bus open OK (recovered)");
                }
                else
                {
                    Exception openError = fresh.LastOpenError;
                    try { fresh.Dispose(); }
                    catch { }
                    _busFailAttempts++;
                    if (openError != null && _busFailAttempts <= 1)
                    {
                        // INC-03 ordered-restart: capacity-skew blocked the sized
                        // view — log the full runbook once (stop consumers, start
                        // producer iCem.exe FIRST, POC after)
                        TapLog.Log("BUS BLOCKED (ordered-restart): " + openError.Message.Replace(Environment.NewLine, " | "));
                    }
                    else if (_busFailAttempts <= 1 || (_busFailAttempts % 6) == 0)
                    {
                        // once per minute max
                        TapLog.Log("RETRY bus still disabled (attempt " + _busFailAttempts.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }
            }
            catch
            {
                // never throw from the timer thread
            }
        }

        // ── full poll → com_stream (com map) + lan_stream_crio (lan map, LAN
        //    equipment sources only). Recording-gated: ActiveCase exists only
        //    during a realtime case session. ─────────────────────────────────────

        private void PollStreams()
        {
            if (_owner == null)
            {
                ReportPollSkip("no-window");
                return;
            }
            if (_bus == null || !_bus.IsOk)
            {
                ReportPollSkip("bus-not-ok");
                return;
            }
            CementingProject project = null;
            try
            {
                project = _owner.ProjectManager.CurrentProject as CementingProject;
            }
            catch (Exception ex)
            {
                // vendor ProjectManager failure — surfaced, never fatal
                ReportPollSkip("project-throw:" + ex.GetType().Name);
                return;
            }
            if (project == null)
            {
                ReportPollSkip("no-project");
                return;
            }
            CementCase cementCase = null;
            try
            {
                cementCase = project.ActiveCase;
            }
            catch (Exception ex)
            {
                // vendor throws InvalidOperationException when no case is active
                ReportPollSkip("activecase-throw:" + ex.GetType().Name);
                return;
            }
            if (cementCase == null)
            {
                ReportPollSkip("no-active-case");
                return;
            }

            var comCandidates = new List<KeyValuePair<string, object>>(64);
            var lanCandidates = new List<KeyValuePair<string, object>>(128);
            var lanBatches = new List<PollResolution.LanSourceBatch>(16);
            List<string> universe = (_heartbeat % 60) == 0 ? new List<string>(512) : null;

            if (!GatherCandidates(cementCase, comCandidates, lanCandidates, lanBatches, universe))
            {
                ReportPollSkip(BuildGatherSkipReason(comCandidates.Count, lanCandidates.Count));
                return;
            }
            ClearPollSkip();

            if (universe != null)
            {
                LogPollUniverse(universe);
            }

            BusWriter bus = _bus;

            // ── com_stream: source-order candidates, strict convert, first-convertible
            //    claims the address; badtype skips WITHOUT consuming the claim ──
            PollResolution.Outcome com = PollResolution.Resolve(comCandidates);
            Interlocked.Add(ref _badTypeSkips, com.BadType);
            Interlocked.Add(ref _dupSkips, com.Dup);
            foreach (string badName in com.BadTypeNames)
            {
                RecordBadType(badName);
            }

            int comWrites = 0;
            foreach (PollResolution.Entry entry in com.Entries)
            {
                int address = entry.Address;
                PreparedPairWrite request = PollPairPreparation.PrepareCom(entry);
                BusWriteQueue.Post(delegate
                {
                    bus.WritePreparedPair(request);
                    Interlocked.Increment(ref _comPairs);
                });
                comWrites++;
                if (_traceTicksLeft > 0 && address <= BusLayout.ComStreamBaseReg + 8)
                {
                    TapLog.Log("COMTRACE addr=" + address
                        + " name='" + entry.Name + "'"
                        + " val=" + entry.Value.ToString("R", CultureInfo.InvariantCulture)
                        + " bits=0x" + BitConverter.DoubleToInt64Bits(entry.Value).ToString("X16", CultureInfo.InvariantCulture));
                }
            }
            // INC-12: the com/lan FSMs are evidence-driven via UpdateStaleness
            // (BusWriter.LastLivenessMarkUtc — fresh writes only). A frozen
            // rewrite batch keeps the data current but must NOT keep the FSM
            // Active — the flag drop after the idle window is the point.

            // ── lan_stream_crio: resolution law over the lan map with the INC-13
            //    fresh-first tie-break among duplicate sources (per-source batches);
            //    writes are full 4-byte CDAB (INC-06 fix — NO byte-width
            //    truncation, ever) ──
            PollResolution.Outcome lan = PollResolution.ResolveLan(lanBatches);
            Interlocked.Add(ref _badTypeSkips, lan.BadType);
            Interlocked.Add(ref _dupSkips, lan.Dup);
            foreach (string badName in lan.BadTypeNames)
            {
                RecordBadType(badName);
            }

            // resolver-choice evidence: WinsBySource → per-source trackers
            foreach (LanSourceTracker tracker in _lanSourceOrder)
            {
                int wins;
                lan.WinsBySource.TryGetValue(tracker.Key, out wins);
                tracker.Won = wins;
            }
            EmitLanSourceDiagnostics();

            int lanWrites = 0;
            foreach (PollResolution.Entry entry in lan.Entries)
            {
                int address = entry.Address;
                PreparedPairWrite request = PollPairPreparation.PrepareLan(entry);
                BusWriteQueue.Post(delegate
                {
                    bus.WritePreparedPair(request);
                    Interlocked.Increment(ref _lanPairs);
                });
                lanWrites++;
            }
            if (lanWrites > 0)
            {
                // D4: lan liveness block = AddIn-owned. INC-12: the FSM is
                // evidence-driven (UpdateStaleness) — a frozen rewrite batch
                // must NOT keep it Active.
                if ((_lanLogGate++ % 10) == 0)
                {
                    TapLog.Log("LAN pairs=" + lanWrites.ToString(CultureInfo.InvariantCulture)
                        + " (crio 200..399, full CDAB)");
                }
            }

            if (_traceTicksLeft > 0)
            {
                Interlocked.Decrement(ref _traceTicksLeft);
            }
            TapLog.Log(string.Format("MAIN case={0} comPairs={1} lanPairs={2} skipped={3}/{4} dup={5} badtype={6} cand={7}/{8} srcs={9} lanSrc={10} vars={11}",
                cementCase.Name, comWrites, lanWrites,
                Interlocked.Read(ref _skippedCom), Interlocked.Read(ref _skippedLan),
                Interlocked.Read(ref _dupSkips), Interlocked.Read(ref _badTypeSkips),
                comCandidates.Count, lanCandidates.Count,
                _gatherSourceCount, _gatherLanSourceCount, _gatherVariableCount));
        }

        // ── POLL skip diagnostics: reason-tagged, logged on state CHANGE and at
        //    most every 10 s while the skip persists (24/7 log hygiene). The
        //    owner's stand question "raw flows but com/lan empty — why?" is
        //    answered by exactly one of these lines in TapOut.log. ────────────

        /// <summary>Record + (throttled) log one poll skip reason. Call sites =
        /// every silent early-return of PollStreams — no gate stays silent.</summary>
        private void ReportPollSkip(string reason)
        {
            string prev = _pollSkipReason;
            _pollSkipReason = reason;
            bool changed = !string.Equals(prev, reason, StringComparison.Ordinal);
            LogPollSkipIfDue(reason, DateTime.UtcNow, changed);
        }

        /// <summary>Poll reached the write path after skips (or fresh): clear
        /// the reason; log the transition once so stand logs show recovery.</summary>
        private void ClearPollSkip()
        {
            string prev = _pollSkipReason;
            _pollSkipReason = null;
            if (prev != null)
            {
                TapLog.Log("POLL RESUME prev-skip=" + prev + " hb=" + _heartbeat.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void LogPollSkipIfDue(string reason, DateTime now, bool changed)
        {
            try
            {
                if (!changed && (now - _lastSkipLogUtc).TotalSeconds < 10.0)
                {
                    return; // same reason, inside the 10 s reminder window
                }
                _lastSkipLogUtc = now;
                TapLog.Log("POLL SKIP reason=" + reason
                    + " hb=" + _heartbeat.ToString(CultureInfo.InvariantCulture)
                    + " (com/lan poll gated — raw lane unaffected)");
            }
            catch
            {
                // diagnostics never take the timer down
            }
        }

        /// <summary>Detail for the gather-empty skip: how many vendor sources /
        /// variables were visible and how many carried LAN affinity — this
        /// distinguishes "no sources at all" (provider never populated: no
        /// realtime case of a non-Design type, or no equipment assigned to it)
        /// from "sources exist but zero names matched the bus maps".</summary>
        private string BuildGatherSkipReason(int comCand, int lanCand)
        {
            string baseReason = "gather-empty srcs=" + _gatherSourceCount.ToString(CultureInfo.InvariantCulture)
                + " lanSrc=" + _gatherLanSourceCount.ToString(CultureInfo.InvariantCulture)
                + " vars=" + _gatherVariableCount.ToString(CultureInfo.InvariantCulture)
                + " lanVars=" + _gatherLanVariableCount.ToString(CultureInfo.InvariantCulture)
                + " comCand=" + comCand.ToString(CultureInfo.InvariantCulture)
                + " lanCand=" + lanCand.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(_gatherError))
            {
                return baseReason + " err=" + _gatherError;
            }
            return baseReason;
        }

        // ── raw COM line → raw_com_line (D1 latch: ONLY the latched device's
        //    line lands on the bus; others are discarded — first-valid-wins) ───

        private void OnComPortPropertyChanged(object sender, ComPortPropertyChangedEventArgs e)
        {
            try
            {
                if (e == null || !string.Equals(e.PropertyName, "DataStream", StringComparison.Ordinal))
                {
                    return;
                }
                IPort port = e.ComPort;
                if (port == null)
                {
                    return;
                }
                if (port.State != ComPortStates.Streaming && port.State != ComPortStates.Recording)
                {
                    return; // ≈ engine IsRunning gate
                }
                string line = port.DataStream;
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }
                int equipmentPort = (int)port.EquipmentPort; // None=0, COM1..16, LAN=17
                if (equipmentPort < 1 || equipmentPort > 16)
                {
                    return; // serial COM devices only (raw COM line, not LAN)
                }

                // D1: first-valid-wins latch — lines from other devices are
                // DISCARDED (never reach raw_com_line)
                if (!_latch.OnLine(equipmentPort.ToString(CultureInfo.InvariantCulture), equipmentPort))
                {
                    Interlocked.Increment(ref _latchDiscards);
                    return;
                }

                if (_bus == null || !_bus.IsOk)
                {
                    return;
                }

                BusWriter bus = _bus;
                BusWriteQueue.Post(delegate
                {
                    bus.WriteRawLine(line);
                    Interlocked.Increment(ref _rawLines);
                });
                // INC-12: raw keeps line-ARRIVAL liveness — but the FSM evidence
                // comes from the writer (UpdateStaleness), not from this event.
                if ((_rawLogGate++ % 5) == 0)
                {
                    TapLog.Log(string.Format("RAW port=COM{0} chars={1} line={2}",
                        equipmentPort, line.Length.ToString(CultureInfo.InvariantCulture), line));
                }
            }
            catch
            {
                // never propagate into the vendor PropertyChanged pipeline
            }
        }

        // ── staleness: flip zone activeFlag to 0 when the source goes quiet
        //    (A4/A5). Body registers are NEVER zeroed (STALE-HOLD). D4: com ∧
        //    lan ∧ raw liveness blocks are ALL AddIn-owned (file-header bytes
        //    0..63).
        //    INC-12 (2026-08-29): the FSM consumes the WRITER's fresh-write
        //    timestamps (BusWriter.LastLivenessMarkUtc — ONE change detector,
        //    file-seeded) instead of marking itself per posted write: frozen
        //    same-value rewrites keep the data current but stop advancing the
        //    evidence ⇒ the FSM honestly drops the flag after the idle window,
        //    and the write itself re-raises it on the next fresh value. ────

        private void UpdateStaleness()
        {
            if (_bus == null || !_bus.IsOk)
            {
                return;
            }
            DateTime now = DateTime.UtcNow;
            BusWriter bus = _bus;
            // ZoneLiveness.Poll = the Active→Idle transition (statechart-as-pattern,
            // total transition table in ZoneLiveness.cs): true exactly ONCE per
            // crossing ⇒ exactly one SetZoneActive post per loss episode (no bounce).
            TrackZoneEvidence(bus.LastLivenessMarkUtc(ZoneKind.ComMain), _comLiveness, ref _lastSeenComFreshUtc, now);
            if (_comLiveness.Poll(now, IdleTimeout))
            {
                BusWriteQueue.Post(delegate { bus.SetZoneActive(ZoneKind.ComMain, false); });
            }
            TrackZoneEvidence(bus.LastLivenessMarkUtc(ZoneKind.Lan), _lanLiveness, ref _lastSeenLanFreshUtc, now);
            if (_lanLiveness.Poll(now, IdleTimeout))
            {
                BusWriteQueue.Post(delegate { bus.SetZoneActive(ZoneKind.Lan, false); });
            }
            TrackZoneEvidence(bus.LastLivenessMarkUtc(ZoneKind.RawReserve), _rawLiveness, ref _lastSeenRawFreshUtc, now);
            if (_rawLiveness.Poll(now, IdleTimeout))
            {
                BusWriteQueue.Post(delegate { bus.SetZoneActive(ZoneKind.RawReserve, false); });
            }
        }

        /// <summary>Feed one zone's FSM from the writer's fresh-evidence clock:
        /// the timestamp advancing = a FRESH write landed ⇒ MarkWritten (any
        /// state → Active — the write itself raised the header flag). A parked
        /// timestamp = frozen rewrites only ⇒ no re-activation; Poll handles
        /// the single Active→Idle drop.</summary>
        private static void TrackZoneEvidence(DateTime freshUtc, ZoneLiveness fsm, ref DateTime lastSeenUtc, DateTime now)
        {
            if (freshUtc > lastSeenUtc)
            {
                lastSeenUtc = freshUtc;
                fsm.MarkWritten(now);
            }
        }

        // ── vendor snapshot helpers ────────────────────────────────────────────

        /// <summary>
        /// Enumerate the full poll universe once per tick (vendor objects on the UI
        /// dispatcher). Source affinity: EquipmentVariableSource with SourceComPort
        /// == 17 (EquipmentPort.LAN) feeds the LAN candidate list; every other
        /// source feeds the COM candidate list. Per variable: name collected; VALUE
        /// is read ONLY for names relevant to the bus (map-matched) — unmatched
        /// names never touch vendor value objects (minimal vendor-object churn).
        /// LAN sources additionally produce a PollResolution.LanSourceBatch
        /// (INC-13) with the caller-computed Fresh verdict: the freshest vendor
        /// EquipmentSourceVariable holder CurrentTime (stamped by the vendor's
        /// PartialResultsHaveBeenPersisted path per persisted EquipmentData)
        /// advanced within LanFreshWindow ⇒ the equipment is delivering frames.
        /// Universe list is filled only when the caller passes a collector
        /// (POLLUNIV dump, once per 60 s).
        /// </summary>
        private bool GatherCandidates(CementCase cementCase,
            List<KeyValuePair<string, object>> comOut,
            List<KeyValuePair<string, object>> lanOut,
            List<PollResolution.LanSourceBatch> lanBatches,
            List<string> universeOut)
        {
            Func<bool> gather = delegate
            {
                bool any = false;
                int sourceCount = 0;
                int lanSourceCount = 0;
                int variableCount = 0;
                int lanVariableCount = 0;
                var currentOrder = new List<LanSourceTracker>();
                DateTime nowUtc = DateTime.UtcNow;
                _gatherError = null; // per-tick: only a CURRENT vendor failure is reported
                try
                {
                    VariableProvider provider = VariableProviderFactory.Instance.GetVariableProvider(cementCase);
                    List<IVariableSource> sources = (provider != null) ? provider.VariableSources.ToList() : null;
                    if (sources != null)
                    {
                        foreach (IVariableSource source in sources)
                        {
                            if (source == null)
                            {
                                continue;
                            }
                            sourceCount++;
                            bool lanEquipment;
                            EquipmentVariableSource eq = null;
                            try
                            {
                                eq = source as EquipmentVariableSource;
                                lanEquipment = eq != null && eq.SourceComPort == LanEquipmentPort;
                            }
                            catch
                            {
                                eq = null;
                                lanEquipment = false;
                            }
                            if (lanEquipment)
                            {
                                lanSourceCount++;
                            }
                            PollResolution.LanSourceBatch batch = null;
                            LanSourceTracker tracker = null;
                            DateTime maxCurrentTimeUtc = DateTime.MinValue;
                            if (lanEquipment)
                            {
                                batch = new PollResolution.LanSourceBatch
                                {
                                    SourceKey = BuildLanSourceKey(eq),
                                    Fresh = false
                                };
                                lanBatches.Add(batch);
                                string key = batch.SourceKey;
                                if (!_lanTrackers.TryGetValue(key, out tracker))
                                {
                                    tracker = new LanSourceTracker(key);
                                    _lanTrackers[key] = tracker;
                                }
                                tracker.Equipment = SafeVendorString(() => eq.ContainerDescription);
                                tracker.UnitNumber = ResolveLanUnitNumber(eq);
                                tracker.RecordName = SafeVendorString(() => eq.RecordName);
                                tracker.Online = SafeVendorBool(() => eq.IsOnline);
                                currentOrder.Add(tracker);
                            }
                            List<IVariable> sourceVariables;
                            try
                            {
                                sourceVariables = source.Variables.ToList();
                            }
                            catch
                            {
                                continue; // collection mutated mid-snapshot
                            }
                            foreach (IVariable variable in sourceVariables)
                            {
                                try
                                {
                                    if (variable == null)
                                    {
                                        continue;
                                    }
                                    string name = variable.VariableName;
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        continue;
                                    }
                                    variableCount++;
                                    if (lanEquipment)
                                    {
                                        lanVariableCount++;
                                    }
                                    if (universeOut != null)
                                    {
                                        universeOut.Add(name);
                                    }
                                    int address;
                                    bool matched = lanEquipment
                                        ? DataMonitorAddressMap.TryGetLanAddress(name, out address)
                                        : DataMonitorAddressMap.TryGetComAddress(name, out address);
                                    if (!matched)
                                    {
                                        if (lanEquipment)
                                        {
                                            Interlocked.Increment(ref _skippedLan);
                                        }
                                        else
                                        {
                                            Interlocked.Increment(ref _skippedCom);
                                        }
                                        RecordMiss(name);
                                        continue;
                                    }
                                    // relevant name: read the raw value (strict convert later);
                                    // LAN: also harvest the vendor freshness stamp — the SAME
                                    // cached holder object, plain field read (no extra
                                    // vendor-object churn beyond the existing .Value read)
                                    object raw = null;
                                    DateTime? currentTime = null;
                                    try
                                    {
                                        var wrapper = variable.Value;
                                        if (wrapper != null)
                                        {
                                            raw = wrapper.Value;
                                            if (lanEquipment)
                                            {
                                                currentTime = wrapper.CurrentTime;
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        raw = null; // unreadable ⇒ badtype candidate
                                    }
                                    if (lanEquipment)
                                    {
                                        if (currentTime.HasValue && currentTime.Value > maxCurrentTimeUtc)
                                        {
                                            maxCurrentTimeUtc = currentTime.Value;
                                        }
                                        lanOut.Add(new KeyValuePair<string, object>(name, raw));
                                        batch.Candidates.Add(new KeyValuePair<string, object>(name, raw));
                                    }
                                    else
                                    {
                                        comOut.Add(new KeyValuePair<string, object>(name, raw));
                                    }
                                    any = true;
                                }
                                catch
                                {
                                    // per-variable guard
                                }
                            }
                            if (lanEquipment)
                            {
                                // freshness verdict for this source (INC-13): the vendor
                                // stamps Value.CurrentTime per persisted EquipmentData;
                                // a frozen holder stops advancing ⇒ stale after the window
                                double age = maxCurrentTimeUtc == DateTime.MinValue
                                    ? double.NaN
                                    : Math.Max(0.0, (nowUtc - maxCurrentTimeUtc).TotalSeconds);
                                tracker.AgeSeconds = age;
                                tracker.Fresh = !double.IsNaN(age) && age <= LanFreshWindow.TotalSeconds;
                                batch.Fresh = tracker.Fresh; // INC-13 fix-1: the RESOLVER verdict must ride on the batch, not only the tracker
                                tracker.Observe(batch.Candidates);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // provider/source enumeration failure — surfaced via the
                    // POLL SKIP gather-empty line (err=), never fatal
                    _gatherError = ex.GetType().Name;
                }
                _gatherSourceCount = sourceCount;
                _gatherLanSourceCount = lanSourceCount;
                _gatherVariableCount = variableCount;
                _gatherLanVariableCount = lanVariableCount;
                _lanSourceOrder = currentOrder;
                return any;
            };

            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                try
                {
                    return (bool)app.Dispatcher.Invoke(gather, DispatcherPriority.Background);
                }
                catch
                {
                    return false;
                }
            }
            return gather();
        }

        /// <summary>
        /// POLLUNIV dump: the FULL poll universe (every VariableName from every
        /// source, unique, first-seen order) — raw→normalized form shown when they
        /// differ. Once per 60 s. Evidence channel for alias/map decisions.
        /// </summary>
        private static void LogPollUniverse(List<string> names)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder(2048);
                int count = 0;
                foreach (string raw in names)
                {
                    if (raw == null || !seen.Add(raw))
                    {
                        continue;
                    }
                    string norm = NameNormalizer.Normalize(raw);
                    count++;
                    if (sb.Length > 0)
                    {
                        sb.Append('|');
                    }
                    if (norm == raw)
                    {
                        sb.Append(raw);
                    }
                    else
                    {
                        sb.Append(raw).Append('\u2192').Append(norm); // →
                    }
                }
                TapLog.Log("POLLUNIV n=" + count.ToString(CultureInfo.InvariantCulture) + " " + sb);
            }
            catch
            {
                // swallowed — diagnostics only
            }
        }

        private void RecordMiss(string name)
        {
            try
            {
                lock (_skipLock)
                {
                    long count;
                    _missNames.TryGetValue(name, out count);
                    _missNames[name] = count + 1;
                }
            }
            catch
            {
                // swallowed — diagnostics only
            }
        }

        private void RecordBadType(string name)
        {
            try
            {
                lock (_skipLock)
                {
                    long count;
                    _badTypeNames.TryGetValue(name, out count);
                    _badTypeNames[name] = count + 1;
                }
            }
            catch
            {
                // swallowed — diagnostics only
            }
        }

        private void DumpSkipTop()
        {
            try
            {
                string missDump;
                string badDump;
                lock (_skipLock)
                {
                    // 24/7 log hygiene: SKIPTOP runs at most once per 60 ticks
                    // (TimerTick gate) and stays SILENT when there is nothing to
                    // report — zero skip names ⇒ no log line at all.
                    if (_missNames.Count == 0 && _badTypeNames.Count == 0)
                    {
                        return;
                    }
                    missDump = string.Join(" | ", _missNames
                        .OrderByDescending(kv => kv.Value)
                        .Take(20)
                        .Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));
                    badDump = string.Join(" | ", _badTypeNames
                        .OrderByDescending(kv => kv.Value)
                        .Take(20)
                        .Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));
                }
                TapLog.Log("SKIPTOP MISS " + missDump
                    + " || BADTYPE " + badDump
                    + " || badtype=" + Interlocked.Read(ref _badTypeSkips)
                    + " dup=" + Interlocked.Read(ref _dupSkips)
                    + " latchDiscards=" + Interlocked.Read(ref _latchDiscards)
                    + " latchReleases=" + Interlocked.Read(ref _latchReleases));
            }
            catch
            {
                // swallowed — diagnostics only
            }
        }

        // ── INC-13 LAN source diagnostics: one LANSRC line per LAN source
        //    (identity + freshness + resolver choice + changed counter), emitted
        //    on state change and at most every 60 s (the TimerTick cadence).
        //    Answers «какой источник живой, какой замороженный, кого выбрал
        //    resolver» from TapOut.log alone. ─────────────────────────────────

        /// <summary>Stable per-source identity key (equipment display name +
        /// LAN unit number + device family record name). Vendor-free fallbacks
        /// keep the key usable when reflection/vendor probes fail.</summary>
        private static string BuildLanSourceKey(EquipmentVariableSource eq)
        {
            string name = null;
            string record = null;
            try { name = eq.ContainerDescription; } catch { }
            try { record = eq.RecordName; } catch { }
            return (name ?? LanUnitQuestionMark) + "|" + (record ?? LanUnitQuestionMark) + "|" + ResolveLanUnitNumber(eq);
        }

        private static string SafeVendorString(Func<string> probe)
        {
            try
            {
                string value = probe();
                return string.IsNullOrEmpty(value) ? LanUnitQuestionMark : value;
            }
            catch
            {
                return LanUnitQuestionMark;
            }
        }

        private static bool SafeVendorBool(Func<bool> probe)
        {
            try
            {
                return probe();
            }
            catch
            {
                return false;
            }
        }

        // vendor identity reach-down (diagnostics only): EquipmentVariableSource
        // hides its Equipment; LanUnitNumber (e.g. "11038534" = 0x00A86F46) is
        // the identity that ties a source to the LAN equipment table
        private static System.Reflection.FieldInfo s_equipmentField;
        private static bool s_equipmentFieldResolved;

        private static string ResolveLanUnitNumber(EquipmentVariableSource eq)
        {
            try
            {
                if (eq == null)
                {
                    return LanUnitQuestionMark;
                }
                if (!s_equipmentFieldResolved)
                {
                    s_equipmentField = typeof(EquipmentVariableSource).GetField("_equipment",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    s_equipmentFieldResolved = true;
                }
                if (s_equipmentField == null)
                {
                    return LanUnitQuestionMark;
                }
                object equipment = s_equipmentField.GetValue(eq);
                if (equipment == null)
                {
                    return LanUnitQuestionMark;
                }
                object raw = equipment.GetType().GetProperty("LanUnitNumber")?.GetValue(equipment, null);
                string unit = raw as string;
                if (string.IsNullOrEmpty(unit))
                {
                    return LanUnitQuestionMark;
                }
                ulong hex;
                if (ulong.TryParse(unit, out hex))
                {
                    return "0x" + hex.ToString("X8", CultureInfo.InvariantCulture);
                }
                return unit;
            }
            catch
            {
                return LanUnitQuestionMark;
            }
        }

        /// <summary>Change-triggered + 60 s LANSRC emission. The change signal
        /// is the per-source STATE tuple (fresh/online/matched/won) — the
        /// changed-value counter itself only accumulates between emits.</summary>
        private void EmitLanSourceDiagnostics()
        {
            try
            {
                List<LanSourceTracker> order = _lanSourceOrder;
                if (order.Count == 0)
                {
                    _lanSourceSignature = null;
                    return; // no LAN sources this tick — the POLL SKIP line explains why
                }
                var signature = new System.Text.StringBuilder(256);
                for (int i = 0; i < order.Count; i++)
                {
                    LanSourceTracker tracker = order[i];
                    signature.Append(tracker.Fresh ? '1' : '0')
                        .Append(tracker.Online ? '1' : '0')
                        .Append(':').Append(tracker.Matched.ToString(CultureInfo.InvariantCulture))
                        .Append(':').Append(tracker.Won.ToString(CultureInfo.InvariantCulture))
                        .Append(';');
                }
                string sig = signature.ToString();
                bool changed = !string.Equals(sig, _lanSourceSignature, StringComparison.Ordinal);
                _lanSourceSignature = sig;
                DateTime nowUtc = DateTime.UtcNow;
                bool cadenceDue = (nowUtc - _lanEmitLastUtc).TotalSeconds >= 60.0;
                if (!changed && !cadenceDue)
                {
                    return;
                }
                _lanEmitLastUtc = nowUtc;
                int freshCount = 0;
                var lines = new System.Text.StringBuilder(1024);
                for (int i = 0; i < order.Count; i++)
                {
                    LanSourceTracker tracker = order[i];
                    if (tracker.Fresh)
                    {
                        freshCount++;
                    }
                    string changedPart = tracker.ReadAndResetChanged(nowUtc);
                    lines.Append("LANSRC i=").Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append(" eq=").Append(tracker.Equipment ?? LanUnitQuestionMark)
                        .Append(" unit=").Append(tracker.UnitNumber ?? LanUnitQuestionMark)
                        .Append(" rec=").Append(tracker.RecordName ?? LanUnitQuestionMark)
                        .Append(" online=").Append(tracker.Online ? "1" : "0")
                        .Append(" vars=").Append(tracker.Matched.ToString(CultureInfo.InvariantCulture))
                        .Append(" fresh=").Append(tracker.Fresh ? "1" : "0")
                        .Append(" age=").Append(double.IsNaN(tracker.AgeSeconds)
                            ? "-"
                            : tracker.AgeSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                        .Append("s won=").Append(tracker.Won.ToString(CultureInfo.InvariantCulture))
                        .Append(" changed=").Append(changedPart)
                        .Append('\n');
                }
                lines.Append("LANRESOLVE srcs=").Append(order.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" fresh=").Append(freshCount.ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(order.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" law=fresh-first fallback=first-convertible window=")
                    .Append((int)LanFreshWindow.TotalSeconds).Append('s');
                TapLog.Log(lines.ToString().TrimEnd('\n'));
                PruneLanTrackers(order);
            }
            catch
            {
                // diagnostics never take the timer down
            }
        }

        /// <summary>24/7 hygiene: trackers whose source vanished from the
        /// provider snapshot are dropped once the tracker set grows beyond the
        /// live count (equipment churn is rare; bounded either way).</summary>
        private void PruneLanTrackers(List<LanSourceTracker> live)
        {
            if (_lanTrackers.Count <= live.Count || _lanTrackers.Count <= 64)
            {
                return;
            }
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LanSourceTracker tracker in live)
            {
                keep.Add(tracker.Key);
            }
            var dead = new List<string>();
            foreach (KeyValuePair<string, LanSourceTracker> entry in _lanTrackers)
            {
                if (!keep.Contains(entry.Key))
                {
                    dead.Add(entry.Key);
                }
            }
            foreach (string key in dead)
            {
                _lanTrackers.Remove(key);
            }
        }

        // ── reg 1: caseState — RealtimeState enum passthrough (tap-exposed ONLY;
        //    NO invented states; unknown/unavailable = 0) ───────────────────────

        private short ResolveCaseState()
        {
            try
            {
                if (_owner == null)
                {
                    return BusLayout.CaseStateUnknown;
                }
                CementingProject project = _owner.ProjectManager.CurrentProject as CementingProject;
                if (project == null)
                {
                    return BusLayout.CaseStateUnknown;
                }
                CementCase cementCase = project.ActiveCase; // vendor throws when none
                if (cementCase == null)
                {
                    return BusLayout.CaseStateUnknown;
                }
                RealtimeCase realtimeCase = cementCase.RealtimeCase;
                if (realtimeCase == null)
                {
                    return BusLayout.CaseStateUnknown;
                }
                return (short)realtimeCase.RealtimeState; // 0..5 passthrough
            }
            catch
            {
                return BusLayout.CaseStateUnknown;
            }
        }

        // ── reg 2: caseLanState — LANManager.LANStatus passthrough (ComPortStates
        //    enum, tap-exposed ONLY; unknown/unavailable = 0) ────────────────────

        private static short ResolveCaseLanState()
        {
            try
            {
                LANManager manager = LANManager.Instance;
                if (manager == null)
                {
                    return BusLayout.CaseLanStateNone;
                }
                return (short)manager.LANStatus; // ComPortStates passthrough
            }
            catch
            {
                return BusLayout.CaseLanStateNone;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (disposing)
            {
                try
                {
                    if (_comPortPropertyHandler != null)
                    {
                        ComPortManager.Instance.ComPortPropertyChanged -= _comPortPropertyHandler;
                        _comPortPropertyHandler = null;
                    }
                }
                catch
                {
                    // swallowed
                }
                try
                {
                    if (_hzTimer != null)
                    {
                        _hzTimer.Dispose();
                        _hzTimer = null;
                    }
                }
                catch
                {
                    // swallowed
                }
                try
                {
                    if (_bus != null)
                    {
                        _bus.Dispose();
                        _bus = null;
                    }
                }
                catch
                {
                    // swallowed
                }
            }
        }
    }
}
