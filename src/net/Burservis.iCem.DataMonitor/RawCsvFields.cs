using System;
using System.Collections.Generic;
using System.Globalization;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// raw_com_line CSV field resolution law (contract v5.1, CHANGE 2 user
    /// decree 2026-08-18 — Э2b rows C01..C08). Vendor-free, self-testable.
    /// The last received COM line is split into CSV fields; field i (0-based)
    /// is parsed as a double and lands as a float32 CDAB pair at
    /// 400 + i*2 (BusWriter.WriteRawLine does the writing).
    ///
    /// POSITIONAL OVER THE RAW LINE AS-IS (DA decision 2026-08-18): ALL fields
    /// are indexed, INCLUDING the two excludeFromTelemetry header fields
    /// ('Time &amp; Date', 'Unit Num') — the exclusion law governs the com
    /// telemetry zone (INC-05), NOT this zone. The raw zone's purpose is "what
    /// lands in the string, now as numbers where parseable"; skipping excluded
    /// fields would make every downstream position template-dependent.
    /// SIDE BENEFIT: positional parsing is TEMPLATE-AGNOSTIC — any
    /// ≤100-field line (flecs/plc/unipro/compupac) flows without name
    /// coverage.
    ///
    /// TOTAL law:
    ///   null/empty line ⇒ zero entries (caller treats as no-op);
    ///   unparseable field ⇒ NO entry for that index (caller writes nothing ⇒
    ///     stale-hold retains the last good value — global stale-hold law);
    ///   &gt;MaxFields fields ⇒ truncated at index MaxFields-1 (99);
    ///   parse rule = double.TryParse(NumberStyles.Float, InvariantCulture):
    ///     leading/trailing whitespace, sign, decimal point, exponent allowed.
    /// </summary>
    internal static class RawCsvFields
    {
        /// <summary>Zone capacity in float32 pairs: regs 400..599 = 100 pairs.</summary>
        public const int MaxFields = BusLayout.RawComLineRegCount / 2;

        /// <summary>One resolved field: positional index + parsed value.</summary>
        public sealed class Field
        {
            public readonly int Index;
            public readonly double Value;

            public Field(int index, double value)
            {
                Index = index;
                Value = value;
            }
        }

        /// <summary>Resolve a raw COM line into its parseable positional
        /// fields (index order). Never throws; null/empty ⇒ empty list.</summary>
        public static List<Field> Resolve(string line)
        {
            var fields = new List<Field>(MaxFields);
            if (string.IsNullOrEmpty(line))
            {
                return fields;
            }
            string[] parts = line.Split(',');
            int count = Math.Min(parts.Length, MaxFields);
            for (int i = 0; i < count; i++)
            {
                double value;
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    fields.Add(new Field(i, value));
                }
                // unparseable ⇒ no entry ⇒ the pair is NOT written (stale-hold)
            }
            return fields;
        }
    }
}
