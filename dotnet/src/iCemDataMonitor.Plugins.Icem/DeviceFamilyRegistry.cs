using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

public static class DeviceFamilyRegistry
{
    /// <summary>
    /// Default active config: family "flecs" / template "flecs_ver4p53_ppg_bpm_psi".
    /// F8: MUST resolve to an existing .ssf file. Verified by DefaultResolves().
    /// </summary>
    public const string DefaultFamily = "flecs";
    public const string DefaultTemplate = "flecs_ver4p53_ppg_bpm_psi";

    public static readonly IReadOnlyDictionary<string, string[]> Families = new Dictionary<string, string[]>
    {
        ["flecs"] = new[]
        {
            "flecs_ver4p53_ppg_bpm_psi", "flecs_ver4p53_kgl_lpm_bar",
            "flecs_ver4p53_km3_m3m_mpa", "flecs_ver4p53_sg_lpm_atm",
            "flecs_ver4p16_ppg_bpm_psi", "flecs_ver4p16_kgl_lpm_bar",
            "flecs_ver4p16_km3_m3m_mpa", "flecs_ver4p16_sg_lpm_atm",
            "flecscmt_4_03_english", "flecscmt_3_06_english",
            "flecsadc", "flecsadc30", "flecsadc30_metric", "FlecsCTunit",
        },
        ["plc"] = new[]
        {
            "plc", "PLC_ADC", "PLC_ADC_kgl_lpm_bar", "PLC_ADC_km3_m3m_mpa",
            "PLC_ADC_sg_lpm_atm", "plc_cms", "plc_cms_lpm_m3m",
            "plc_cms_IPGenIV", "PLC_AdvantageOne_New", "AdcPump", "adc_down",
        },
        ["unipro"] = new[]
        {
            "cms", "cms_down", "cmntace", "cmntadc", "compac_unipro",
        },
        ["compupac"] = new[]
        {
            "compac", "cpt300", "ct60079b", "c75tc4", "c75tc4_down",
        },
        ["other"] = new[]
        {
            "wits1generaltimebased", "wits4hydraulics", "wits17cementing",
            "tanklevel", "tanklevelmetric", "coiltube", "hydrtube",
            "n2_foam", "nitout", "nitro_180", "nit_340", "nit_90_660",
            "s&s_blender", "s&s_pump", "prd", "amp",
            "CanadianBodyloadMastCTU",
        },
    };

    /// <summary>
    /// F8 assertion: default family/template must resolve to an existing .ssf file.
    /// Returns null on success, error message on failure.
    /// </summary>
    public static string? VerifyDefaultResolves()
    {
        var path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "templates",
            DefaultTemplate + ".ssf");
        if (!File.Exists(path))
            return $"F8 FAIL: default template '{DefaultTemplate}.ssf' not found at {path}";

        if (!Families.ContainsKey(DefaultFamily))
            return $"F8 FAIL: default family '{DefaultFamily}' not in registry";

        var templates = Families[DefaultFamily];
        if (!templates.Contains(DefaultTemplate))
            return $"F8 FAIL: '{DefaultTemplate}' not in '{DefaultFamily}' family list";

        return null;
    }

    /// <summary>
    /// F8 assertion: every registered template name MUST have a corresponding .ssf file.
    /// Returns list of missing files (empty = all present).
    /// </summary>
    public static IReadOnlyList<string> VerifyAllTemplatesExist()
    {
        var missing = new List<string>();
        var templatesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");

        foreach (var (family, names) in Families)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(templatesDir, name + ".ssf");
                if (!File.Exists(path))
                    missing.Add($"{family}/{name}.ssf");
            }
        }
        return missing;
    }
}
