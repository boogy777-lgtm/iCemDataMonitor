using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Zone liveness FSM (statechart-as-PATTERN — explicit total transition
    /// function, vendor-free; NO library). ONE state machine, THREE instances
    /// (com/lan/raw) — the replicated (flag,timestamp) field pairs and the
    /// scattered if-sites in DataMonitorModule collapse into this class (replication
    /// = where bugs hatch: fix com, forget lan).
    ///
    /// States: Idle ⇄ Active. Events: MarkWritten (a pair/line landed on the
    /// bus), Poll (1 Hz staleness tick).
    ///
    /// Transition table — TOTAL (every state × event defined; Э2b rows
    /// B02/B09/B13/B17..B20 are the same function in CSV form):
    ///   Idle   × MarkWritten            → Active (the WRITE itself raises the
    ///                                      header flag — UpdateZoneHeaderLocked;
    ///                                      the module NEVER posts SetZoneActive(true))
    ///   Idle   × Poll (any silence)     → Idle   (no-op — no repeated posts, no bounce)
    ///   Active × MarkWritten            → Active (evidence refreshed)
    ///   Active × Poll, silence ≤ idle   → Active (boundary == timeout is NOT beyond)
    ///   Active × Poll, silence &gt; idle   → Idle   (caller posts SetZoneActive(zone,
    ///                                      false) — FLAG ONLY, body cells never zeroed)
    /// Post-drop: Poll keeps returning false (exactly ONE drop per crossing);
    /// the next MarkWritten re-enters Active (reconnect).
    /// </summary>
    internal sealed class ZoneLiveness
    {
        private readonly ZoneKind _zone;
        private bool _active;
        private DateTime _lastWriteUtc = DateTime.MinValue;

        public ZoneLiveness(ZoneKind zone)
        {
            _zone = zone;
        }

        public ZoneKind Zone
        {
            get { return _zone; }
        }

        public bool IsActive
        {
            get { return _active; }
        }

        public DateTime LastWriteUtc
        {
            get { return _lastWriteUtc; }
        }

        /// <summary>Event: a value landed on the bus for this zone (any state).</summary>
        public void MarkWritten(DateTime nowUtc)
        {
            _active = true;
            _lastWriteUtc = nowUtc;
        }

        /// <summary>Event: 1 Hz staleness tick. Returns true ⇔ the caller must
        /// post SetZoneActive(Zone, false) — exactly once per Active→Idle crossing.</summary>
        public bool Poll(DateTime nowUtc, TimeSpan idleTimeout)
        {
            if (!ZoneStaleness.ShouldDrop(_active, _lastWriteUtc, nowUtc, idleTimeout))
            {
                return false;
            }
            _active = false;
            return true;
        }
    }
}
