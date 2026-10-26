namespace iCemDataMonitor.Plugins.Icem;

public static class VariableUnitGuesser
{
    /// <summary>
    /// Exact-name lookup table: canonical Mongo keys + SSF display names → correct UOM.
    /// Every flecs canonical key (34) and every plc_basic.json emulator name (10) must appear here.
    /// Phase 1: exact match wins (case-insensitive). Phase 2: substring heuristics.
    /// </summary>
    private static readonly Dictionary<string, string> ExactUom =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── flecs canonical keys (FlecsVariableCatalog.cs) ──
            ["UnixTimestamp"]     = "",
            ["UnitNum"]           = "",
            ["SlurryDensity"]     = "ppg",
            ["DH_Density"]        = "ppg",
            ["MixWaterRate"]      = "bpm",
            ["PumpRate"]          = "bpm",
            ["PumpPressure"]      = "psi",
            ["DSPumpPress"]       = "psi",
            ["WaterStgTot"]       = "bbl",
            ["MixWaterTotal"]     = "bbl",
            ["PumpStgTot"]        = "bbl",
            ["CombPumpTotal"]     = "bbl",
            ["CementValvePsn"]    = "%",
            ["WaterValvePsn"]     = "%",
            ["PSPumpRate"]        = "bpm",
            ["DSPumpRate"]        = "bpm",
            ["DigOut"]            = "",
            ["HalWinEvent"]       = "",
            ["Temperature"]       = "degF",
            ["rho12_hat"]         = "ppg",
            ["h2_hat"]            = "ft",
            ["vhp_density_HP"]    = "ppg",
            ["TubHeight"]         = "ft",
            ["ExtraPress1"]       = "psi",
            ["ExtraPress2"]       = "psi",
            ["ExtraRate1"]        = "bpm",
            ["ExtraTotal1"]       = "bbl",
            ["ExtraRate2"]        = "bpm",
            ["ExtraTotal2"]       = "bbl",
            ["WtrSlryRatio"]      = "",
            ["MudCupDensity"]     = "ppg",
            ["PSPmpStgTot"]       = "bbl",
            ["DSPmpStgTot"]       = "bbl",
            ["VolumetricMeter"]   = "bpm",

            // ── flecs SSF display names (canonical ↔ display bridge) ──
            ["Time & Date"]       = "",
            ["Unit Num"]          = "",
            ["Recirc Density"]    = "ppg",
            ["DH Density"]        = "ppg",
            ["Mix Water Rate"]    = "bpm",
            ["Comb Pump Rate"]    = "bpm",
            ["PS Pump Press"]     = "psi",
            ["DS Pump Press"]     = "psi",
            ["Water Stg Tot"]     = "bbl",
            ["Mix Water Total"]   = "bbl",
            ["Pump Stg Tot"]      = "bbl",
            ["Comb Pump Total"]   = "bbl",
            ["Cmnt Valve Psn"]    = "%",
            ["Water Valve Psn"]   = "%",
            ["PS Pump Rate"]      = "bpm",
            ["DS Pump Rate"]      = "bpm",
            ["Dig out"]           = "",
            ["HalWin Event"]      = "",
            ["Tub Height"]        = "ft",
            ["Extra Press 1"]     = "psi",
            ["Extra Press 2"]     = "psi",
            ["Extra Rate 1"]      = "bpm",
            ["Extra Total 1"]     = "bbl",
            ["Extra Rate 2"]      = "bpm",
            ["Extra Total 2"]     = "bbl",
            ["Wtr/slry Ratio"]    = "",
            ["MudCup Density"]    = "ppg",
            ["PS Pmp Stg Tot"]    = "bbl",
            ["DS Pmp Stg Tot"]    = "bbl",

            // ── plc_basic.json emulator names ──
            ["PLC Timestamp"]     = "",
            ["PLC Status"]        = "",
            ["Suction Pressure"]  = "psi",
            ["Discharge Pressure"]= "psi",
            ["Pump Rate"]         = "bpm",
            ["Flow Rate"]         = "gpm",
            ["Tank Level"]        = "ft",
            ["Tank Temperature"]  = "degF",
            ["Motor Speed"]       = "rpm",
            ["Valve Position"]    = "%",

            // ── common PLC SSF display names ──
            ["Flow 1 Rate"]       = "bpm",
            ["Flow 1 Total"]      = "bbl",
            ["Flow 2 Rate"]       = "bpm",
            ["Flow 2 Total"]      = "bbl",
            ["Pressure 1"]        = "psi",
            ["Pressure 2"]        = "psi",
            ["Recirc Press"]      = "psi",
            ["Recirc FlowRate"]   = "bpm",
            ["Belly MagMeter"]    = "bpm",
            ["Mag Meter Total"]   = "bbl",
            ["Pipe Weight"]       = "klb",
            ["CT Depth"]          = "ft",
            ["CT Stretch"]        = "ft",
            ["CT Speed"]          = "rpm",
            ["CT Run Speed"]      = "rpm",
            ["Depth"]             = "ft",
            ["Tub Level"]         = "ft",
            ["Backside Pr"]       = "psi",
            ["Annulus Pr @WH"]    = "psi",
            ["Back Press Ann"]    = "psi",
            ["Water Rate"]        = "bpm",
            ["Water Total"]       = "bbl",
            ["Cement Rt 1"]       = "bpm",
            ["Running Speed1"]    = "rpm",
            ["TLMS Tub Level"]    = "ft",
            ["TLMS Fdbk Pot"]     = "%",
            ["Cmnt Fdbk Pot"]     = "%",
            ["Water Fdbk Pot"]    = "%",
            ["HT400 PressureA"]   = "psi",
            ["HT400 PressureB"]   = "psi",
            ["HT400 Rate A"]      = "bpm",
            ["HT400 Rate B"]      = "bpm",
            ["Raw PS Press"]      = "psi",
            ["Raw DS Press"]      = "psi",
            ["Raw Side A Pr"]     = "psi",
            ["Raw Side B Pr"]     = "psi",
            ["PS Press At KO"]    = "psi",
            ["DS Press At KO"]    = "psi",
            ["SideA Pr At KO"]    = "psi",
            ["SideB Pr At KO"]    = "psi",
            ["Highest Press"]     = "psi",
        };

    public static string GuessUom(string variableName)
    {
        // Phase 1: exact match (case-insensitive)
        if (ExactUom.TryGetValue(variableName, out var uom))
            return uom;

        // Phase 2: substring heuristics — rules ordered so more specific patterns win
        var n = variableName.ToLowerInvariant();

        // density/concentration → ppg (before "rate" check to avoid false "dens" in names)
        if (n.Contains("density") || n.Contains("dens") || n.Contains("concent"))
            return "ppg";

        // rate/flow/spm/rpm → bpm (checked BEFORE volume/total to prevent "vol" false match)
        if (n.Contains("rate") || n.Contains("flow") || n.Contains("spm") || n.Contains("rpm"))
            return "bpm";

        // temperature
        if (n.Contains("temp"))
            return "degF";

        // pressure → psi
        if (n.Contains("press") || (n.Length >= 2 && n[^2..] == "pr") || n.Contains("pr "))
            return "psi";

        // total/volume/level → bbl (note: substring "vol" safe here ∵ rate check runs first, and VolumetricMeter is in exact table)
        if (n.Contains("total") || n.Contains("volume") || n.Contains("vol") || n.Contains("tot") || n.Contains("level"))
            return "bbl";

        // valve/position
        if (n.Contains("valve") || n.Contains("psn") || n.Contains("position"))
            return "%";

        // height/depth/stretch/tub → ft
        if (n.Contains("height") || n.Contains("depth") || n.Contains("stretch") || n.Contains("tub"))
            return "ft";

        // weight/hookload → klb
        if (n.Contains("weight") || n.Contains("hookload"))
            return "klb";

        return "";
    }

    /// <summary>
    /// Self-check harness: validates that known critical cases produce correct UOM.
    /// Call from a console entry point or unit test.
    /// Returns (passCount, failCount, failures).
    /// </summary>
    public static (int Pass, int Fail, IReadOnlyList<string> Failures) SelfCheck()
    {
        var failures = new List<string>();
        int pass = 0, fail = 0;

        void Check(string name, string expected, string? callerLabel = null)
        {
            var actual = GuessUom(name);
            var label = callerLabel ?? name;
            if (actual == expected) { pass++; }
            else { fail++; failures.Add($"FAIL {label}: '{name}' ⇒ '{actual}' (expected '{expected}')"); }
        }

        // F6 regression: VolumetricMeter must be bpm, not bbl
        Check("VolumetricMeter", "bpm", "F6:VolumetricMeter");
        Check("Mix Water Rate", "bpm", "F6:MixWaterRate");
        Check("Water Stg Tot", "bbl", "F6:WaterStgTot");

        // flecs canonical keys (sample critical ones)
        Check("SlurryDensity", "ppg");
        Check("DH_Density", "ppg");
        Check("PumpRate", "bpm");
        Check("PumpPressure", "psi");
        Check("CombPumpTotal", "bbl");
        Check("CementValvePsn", "%");
        Check("Temperature", "degF");
        Check("TubHeight", "ft");
        Check("UnixTimestamp", "");
        Check("HalWinEvent", "");

        // flecs SSF display names
        Check("Recirc Density", "ppg");
        Check("PS Pump Press", "psi");
        Check("Comb Pump Total", "bbl");
        Check("Cmnt Valve Psn", "%");

        // plc_basic.json emulator names
        Check("PLC Timestamp", "");
        Check("PLC Status", "");
        Check("Suction Pressure", "psi");
        Check("Discharge Pressure", "psi");
        Check("Pump Rate", "bpm");
        Check("Flow Rate", "gpm");
        Check("Tank Level", "ft");
        Check("Tank Temperature", "degF");
        Check("Motor Speed", "rpm");
        Check("Valve Position", "%");

        // substring heuristics: names NOT in exact table
        Check("SomeRandomDensity", "ppg", "sub:density");
        Check("SomeRandomRate", "bpm", "sub:rate");
        Check("SomeRandomPress", "psi", "sub:press");
        Check("SomeRandomTotal", "bbl", "sub:total");
        Check("SomeRandomVolume", "bbl", "sub:volume");
        Check("SomeRandomValve", "%", "sub:valve");
        Check("SomeRandomHeight", "ft", "sub:height");
        Check("SomeRandomWeight", "klb", "sub:weight");
        Check("SomeRandomUnknown", "", "sub:none");

        // edge: "vol" substring must NOT trigger bbl when rate comes first
        Check("VolumetricMeter", "bpm", "edge:vol-but-rate");
        Check("VolumeMeter", "bbl", "edge:volume-no-rate");

        return (pass, fail, failures);
    }
}
