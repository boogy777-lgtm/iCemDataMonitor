using System;
using System.Collections.Generic;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Per-LAN-source poll tracker (vendor-free, INC-13 diagnostics):
    /// remembers the previous tick's raw values per variable name and counts
    /// how many CHANGED since the last readout — the AddIn-local freshness
    /// evidence that makes «какой источник живой, какой замороженный» visible
    /// from TapOut.log without a debugger.
    ///
    /// One tracker per source identity (equipment display name + LAN unit
    /// number + record name). Change detection uses boxed object equality on
    /// the raw wrapper values (1 Hz, ≤ a few hundred names per source — no
    /// allocation-sensitive path). Bounded: names beyond MaxTrackedNames are
    /// ignored (24/7 hygiene), stale trackers are pruned by the module when
    /// their source disappears from the provider snapshot.
    /// </summary>
    internal sealed class LanSourceTracker
    {
        /// <summary>24/7 hygiene cap: tracked names per source.</summary>
        public const int MaxTrackedNames = 4096;

        public readonly string Key;
        public string Equipment;       // vendor ContainerDescription (Equipment.DisplayName)
        public string UnitNumber;      // vendor Equipment.LanUnitNumber via safe reflection, "?" if unreachable
        public string RecordName;      // vendor RecordName (device family)
        public bool Online;            // vendor EquipmentVariableSource.IsOnline snapshot
        public bool Fresh;             // caller-computed: CurrentTime advanced within the window
        public double AgeSeconds = double.NaN; // age of the freshest CurrentTime on the source
        public int Matched;            // map-matched candidates this tick
        public int Won;                // addresses WON in the resolver (WinsBySource tally)

        private int _changedSinceEmit;
        private readonly Dictionary<string, object> _lastRaw =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public LanSourceTracker(string key)
        {
            Key = key ?? string.Empty;
        }

        /// <summary>Observe one tick's candidates (name → raw wrapper value).
        /// Counts candidates whose raw value changed vs the previous tick.
        /// Call once per tick, per source, BEFORE the emit readout.</summary>
        public void Observe(IList<KeyValuePair<string, object>> candidates)
        {
            if (candidates == null)
            {
                return;
            }
            int changed = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                KeyValuePair<string, object> candidate = candidates[i];
                if (candidate.Key == null || _lastRaw.Count >= MaxTrackedNames && !_lastRaw.ContainsKey(candidate.Key))
                {
                    continue;
                }
                object previous;
                if (_lastRaw.TryGetValue(candidate.Key, out previous))
                {
                    if (!Equals(previous, candidate.Value))
                    {
                        changed++;
                    }
                }
                else
                {
                    changed++; // first sighting of the name counts as a change (value appeared)
                }
                _lastRaw[candidate.Key] = candidate.Value;
            }
            _changedSinceEmit += changed;
            Matched = candidates.Count;
        }

        /// <summary>Changed-value total since the last readout, then reset.
        /// Formatted «N/Ms» per the owner's LANSRC log contract (M = seconds
        /// the accumulator spanned).</summary>
        public string ReadAndResetChanged(DateTime nowUtc)
        {
            string result;
            if (_emitUtc == DateTime.MinValue)
            {
                result = _changedSinceEmit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/0s";
            }
            else
            {
                int seconds = (int)(nowUtc - _emitUtc).TotalSeconds;
                if (seconds < 0)
                {
                    seconds = 0;
                }
                result = _changedSinceEmit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            }
            _changedSinceEmit = 0;
            _emitUtc = nowUtc;
            return result;
        }

        private DateTime _emitUtc = DateTime.MinValue;
    }
}
