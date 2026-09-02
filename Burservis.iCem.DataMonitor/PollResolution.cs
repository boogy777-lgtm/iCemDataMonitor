using System;
using System.Collections.Generic;
using System.Globalization;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// com_main per-tick resolution law (vendor-free, self-testable).
    ///
    /// Candidates arrive in SOURCE ORDER, already name-matched to an address by
    /// the caller (DataMonitorAddressMap.TryGetAddress at gather time; re-looked-up
    /// here defensively). Resolution law (operator directive):
    ///
    ///   per address, the FIRST SUCCESSFULLY CONVERTED value wins;
    ///   a candidate that fails strict numeric conversion (DateTime/String/
    ///   bool/NaN/Inf/...) is counted and SKIPPED WITHOUT consuming the address
    ///   claim — a later good candidate still claims the address.
    ///   (dedup-by-address can never drop a good value because of a bad one)
    ///
    ///   LAN AMENDMENT (INC-13, 2026-08-30) — batch overload ResolveLan(batches):
    ///   candidates arrive grouped per SOURCE (LanSourceBatch) with a caller-
    ///   computed FRESH flag (vendor EquipmentSourceVariable holder CurrentTime
    ///   advanced within the freshness window = the equipment is delivering
    ///   frames). Tie-break among duplicate-address claims:
    ///     per address the first FRESH convertible candidate wins; a frozen
    ///     (non-fresh) earlier claim is REPLACED by a later fresh one (the
    ///     displaced candidate counts as Dup). When NO candidate for an address
    ///     is fresh the legacy first-convertible-wins law applies unchanged
    ///     (operator-accepted compromise — fallback when freshness cannot
    ///     differentiate). Flat-list ResolveLan = one non-fresh batch = legacy.
    /// </summary>
    internal static class PollResolution
    {
        public sealed class Entry
        {
            public readonly int Address;
            public readonly string Name;
            public readonly double Value;
            public readonly string SourceKey;

            public Entry(int address, string name, double value)
                : this(address, name, value, null)
            {
            }

            public Entry(int address, string name, double value, string sourceKey)
            {
                Address = address;
                Name = name;
                Value = value;
                SourceKey = sourceKey;
            }
        }

        public sealed class Outcome
        {
            public readonly List<Entry> Entries = new List<Entry>(64);
            public readonly List<string> BadTypeNames = new List<string>(8);
            public int BadType;
            public int Dup;
            /// <summary>Per-source-key winner tally (batch overload only;
            /// flat lists tally under the null key). Resolver-choice evidence.</summary>
            public readonly Dictionary<string, int> WinsBySource = new Dictionary<string, int>();
        }

        /// <summary>One LAN source's contribution for one poll tick: the
        /// map-matched candidates in vendor source order plus the caller's
        /// freshness verdict (vendor CurrentTime advanced within the window).
        /// Vendor-free — the module computes Fresh, this class only carries it.</summary>
        public sealed class LanSourceBatch
        {
            public string SourceKey;
            public bool Fresh;
            public readonly List<KeyValuePair<string, object>> Candidates = new List<KeyValuePair<string, object>>(64);
        }

        /// <summary>
        /// Strict numeric conversion (production-clean rule): ONLY boxed numeric
        /// types (byte..ulong, float, double, decimal) are accepted.
        /// DateTime/String/bool/char/TimeSpan/anything else ⇒ false.
        /// NaN/Infinity ⇒ false. null ⇒ false.
        /// </summary>
        public static bool TryConvert(object raw, out double value)
        {
            value = 0.0;
            if (raw == null)
            {
                return false;
            }
            try
            {
                switch (Type.GetTypeCode(raw.GetType()))
                {
                    case TypeCode.Byte:
                    case TypeCode.SByte:
                    case TypeCode.Int16:
                    case TypeCode.UInt16:
                    case TypeCode.Int32:
                    case TypeCode.UInt32:
                    case TypeCode.Int64:
                    case TypeCode.UInt64:
                    case TypeCode.Single:
                    case TypeCode.Double:
                    case TypeCode.Decimal:
                        value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                        return !double.IsNaN(value) && !double.IsInfinity(value);
                    default:
                        return false; // DateTime, String, Boolean, Char, TimeSpan, ...
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve one tick's candidates (source order). First-convertible-wins
        /// per address; badtype candidates never claim; duplicates dropped.
        /// COM zone lookup (com_stream map).
        /// </summary>
        public static Outcome Resolve(List<KeyValuePair<string, object>> candidates)
        {
            return ResolveWith(candidates, LookupCom);
        }

        /// <summary>
        /// Same resolution law over the LAN zone lookup (lan_stream_crio map —
        /// bus register map v5). Flat-list form = legacy first-convertible-wins
        /// (one non-fresh batch; freshness cannot differentiate).
        /// </summary>
        public static Outcome ResolveLan(List<KeyValuePair<string, object>> candidates)
        {
            var batch = new LanSourceBatch();
            if (candidates != null)
            {
                batch.Candidates.AddRange(candidates);
            }
            return ResolveLan(new List<LanSourceBatch> { batch });
        }

        /// <summary>
        /// LAN resolution over per-source batches (INC-13): per address the
        /// first FRESH convertible candidate wins; a fresh candidate replaces a
        /// frozen earlier claim (displaced candidate → Dup). No fresh candidate
        /// for the address ⇒ legacy first-convertible-wins fallback.
        /// </summary>
        public static Outcome ResolveLan(IReadOnlyList<LanSourceBatch> batches)
        {
            Outcome outcome = new Outcome();
            if (batches == null)
            {
                return outcome;
            }
            var claimed = new Dictionary<int, Claim>();
            var claimOrder = new List<int>();
            foreach (LanSourceBatch batch in batches)
            {
                if (batch == null || batch.Candidates == null)
                {
                    continue;
                }
                foreach (KeyValuePair<string, object> candidate in batch.Candidates)
                {
                    int address;
                    if (!LookupLan(candidate.Key, out address))
                    {
                        continue; // defensive: caller pre-filtered; never counts as miss here
                    }
                    double value;
                    if (!TryConvert(candidate.Value, out value))
                    {
                        outcome.BadType++;
                        outcome.BadTypeNames.Add(candidate.Key);
                        continue; // does NOT consume the address claim
                    }
                    Claim existing;
                    if (claimed.TryGetValue(address, out existing))
                    {
                        outcome.Dup++;
                        if (batch.Fresh && !existing.Fresh)
                        {
                            // fresh source overrides the frozen claim (mis-pick repair);
                            // the displaced candidate was counted as Dup above
                            existing.Fresh = true;
                            existing.Entry = new Entry(address, candidate.Key, value, batch.SourceKey);
                        }
                        continue;
                    }
                    claimed[address] = new Claim
                    {
                        Fresh = batch.Fresh,
                        Entry = new Entry(address, candidate.Key, value, batch.SourceKey)
                    };
                    claimOrder.Add(address);
                }
            }
            foreach (int address in claimOrder)
            {
                Entry entry = claimed[address].Entry;
                outcome.Entries.Add(entry);
                // null key (flat-list legacy path) tallies under the empty key
                string tallyKey = entry.SourceKey ?? string.Empty;
                int wins;
                outcome.WinsBySource.TryGetValue(tallyKey, out wins);
                outcome.WinsBySource[tallyKey] = wins + 1;
            }
            return outcome;
        }

        private sealed class Claim
        {
            public bool Fresh;
            public Entry Entry;
        }

        private delegate bool AddressLookup(string name, out int address);

        private static bool LookupCom(string name, out int address)
        {
            return DataMonitorAddressMap.TryGetComAddress(name, out address);
        }

        private static bool LookupLan(string name, out int address)
        {
            return DataMonitorAddressMap.TryGetLanAddress(name, out address);
        }

        private static Outcome ResolveWith(List<KeyValuePair<string, object>> candidates,
            AddressLookup lookup)
        {
            Outcome outcome = new Outcome();
            if (candidates == null)
            {
                return outcome;
            }
            var seen = new HashSet<int>();
            foreach (KeyValuePair<string, object> candidate in candidates)
            {
                int address;
                if (!lookup(candidate.Key, out address))
                {
                    continue; // defensive: caller pre-filtered; never counts as miss here
                }
                double value;
                if (!TryConvert(candidate.Value, out value))
                {
                    outcome.BadType++;
                    outcome.BadTypeNames.Add(candidate.Key);
                    continue; // does NOT consume the address claim
                }
                if (!seen.Add(address))
                {
                    outcome.Dup++;
                    continue;
                }
                outcome.Entries.Add(new Entry(address, candidate.Key, value));
            }
            return outcome;
        }
    }
}
