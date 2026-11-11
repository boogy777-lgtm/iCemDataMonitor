using System.Text;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Display-name normalization for the address-map fallback (DEFECT-1 fix).
    ///
    /// RULE (exact): keep ONLY ASCII letters and digits; lowercase letters;
    /// everything else (whitespace, punctuation, symbols, non-ASCII) is
    /// stripped. Applied to BOTH sides — poll VariableName and every map key
    /// (tapName/wireName/alias/canonical) — before compare.
    ///
    ///   "Slurry Density"  → "slurrydensity"
    ///   "SlurryDensity"   → "slurrydensity"   (same)
    ///   "Wtr/slry Ratio"  → "wtrslryratio"
    ///   "rho12_hat"       → "rho12hat"
    ///   "Pump Pressure"   → "pumppressure"
    ///
    /// NOT a stemmer/fuzzy match: "Pumping Pressure" → "pumpingpressure" does
    /// NOT equal "pumppressure" — different words stay different by design.
    /// </summary>
    internal static class NameNormalizer
    {
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c >= 'a' && c <= 'z')
                {
                    sb.Append(c);
                }
                else if (c >= 'A' && c <= 'Z')
                {
                    sb.Append((char)(c - 'A' + 'a'));
                }
                else if (c >= '0' && c <= '9')
                {
                    sb.Append(c);
                }
                // everything else stripped: space, tab, &, /, _, ., -, unicode
            }
            return sb.ToString();
        }
    }
}
