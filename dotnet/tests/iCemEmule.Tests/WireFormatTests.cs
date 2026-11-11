using Halliburton.iCemEmule.Core;

using Xunit;

namespace iCemEmule.WireFormat;

/// <summary>
/// Golden-line tests for the FLECS serial wire format (com_wire, CONTRACT_iCemEmule.yaml).
///
/// Wire-fix 2026-08-17 evidence: iCem's CMT v4.53 parser
/// (Halliburton.iCem.RealTime.SerialDataSetup.Connectors.FlecsOrPlcSerialConnector.ParseLine)
/// splits the incoming line on ',' and consumes exactly one token per EQUIPMENT variable.
/// "Time & Date" and "Unit Num" are NOT equipment variables
/// (SerialSetup.IsEquipmentVariable returns false for both) and consume no wire tokens.
/// The real FLECS CMT 4.53 line therefore = 32 comma-separated telemetry fields
/// (Recirc Density .. VolumetricMeter), no leading time/unit columns.
/// variable_registry.json flags these columns excludeFromTelemetry=true —
/// FormatLine must omit them.
/// </summary>
public class FormatLineTests
{
    private static DeviceTemplate BuildFlecs453Template()
    {
        // Mirror of templates/flecs_4.53.json resolved against variable_registry.json.
        var vars = new TemplateVariable[]
        {
            new() { Index = 0,  Name = "Time & Date",     Format = "elapsed", ExcludeFromTelemetry = true },
            new() { Index = 1,  Name = "Unit Num",        Format = "int",     ExcludeFromTelemetry = true },
            new() { Index = 2,  Name = "SlurryDensity" },
            new() { Index = 3,  Name = "DH_Density" },
            new() { Index = 4,  Name = "Mix Water Rate" },
            new() { Index = 5,  Name = "PumpRate" },
            new() { Index = 6,  Name = "PumpPressure" },
            new() { Index = 7,  Name = "DS Pump Press" },
            new() { Index = 8,  Name = "Water Stg Tot" },
            new() { Index = 9,  Name = "Mix Water Total" },
            new() { Index = 10, Name = "Pump Stg Tot" },
            new() { Index = 11, Name = "CombPumpTotal" },
            new() { Index = 12, Name = "CementValvePsn" },
            new() { Index = 13, Name = "WaterValvePsn" },
            new() { Index = 14, Name = "PS Pump Rate" },
            new() { Index = 15, Name = "DS Pump Rate" },
            new() { Index = 16, Name = "Dig out",         Format = "int", TogglePeriod = 2.5 },
            new() { Index = 17, Name = "HalWin Event",    Format = "int", TogglePeriod = 10 },
            new() { Index = 18, Name = "Temperature" },
            new() { Index = 19, Name = "rho12_hat" },
            new() { Index = 20, Name = "h2_hat" },
            new() { Index = 21, Name = "vhp_density_HP" },
            new() { Index = 22, Name = "TubHeight" },
            new() { Index = 23, Name = "Extra Press 1" },
            new() { Index = 24, Name = "Extra Press 2" },
            new() { Index = 25, Name = "Extra Rate 1" },
            new() { Index = 26, Name = "Extra Total 1" },
            new() { Index = 27, Name = "Extra Rate 2" },
            new() { Index = 28, Name = "Extra Total 2" },
            new() { Index = 29, Name = "Wtr/slry Ratio" },
            new() { Index = 30, Name = "MudCup Density" },
            new() { Index = 31, Name = "PS Pmp Stg Tot" },
            new() { Index = 32, Name = "DS Pmp Stg Tot" },
            new() { Index = 33, Name = "VolumetricMeter" },
        };
        return new DeviceTemplate
        {
            Name = "flecs 4.53",
            DeviceType = "FLECS",
            AutoSelectIndices = new[] { 6, 5, 2, 3, 18, 11, 13, 22, 12, 33 },
            Variables = vars,
        };
    }

    [Fact]
    public void Flecs453_GoldenLine_32CommaSeparatedFields_NoTimeNoUnit()
    {
        var template = BuildFlecs453Template();
        // Fixed input vector: values[i] = i for i in 0..33.
        double[] values = Enumerable.Range(0, 34).Select(i => (double)i).ToArray();

        string line = SignalWireCodec.FormatLine(values, template);

        // Expected: idx0 (Time & Date, elapsed) and idx1 (Unit Num) omitted;
        // idx2..15 F2, idx16..17 int, idx18..33 F2.
        const string expected =
            "2.00,3.00,4.00,5.00,6.00,7.00,8.00,9.00,10.00,11.00,12.00,13.00,14.00,15.00," +
            "16,17," +
            "18.00,19.00,20.00,21.00,22.00,23.00,24.00,25.00,26.00,27.00,28.00,29.00,30.00,31.00,32.00,33.00";

        Assert.Equal(expected, line);
        Assert.Equal(32, line.Split(',').Length);
        // No CRLF inside FormatLine (emitter appends it).
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Flecs453_FromCatalog_Emits32Fields()
    {
        var templates = TemplateCatalog.Load();
        var template = templates.FirstOrDefault(t =>
            string.Equals(t.DeviceType, "FLECS", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("FLECS template not found in catalog");

        // Fixed input: values[i] = i.
        var values = Enumerable.Range(0, template.Variables.Length).Select(i => (double)i).ToArray();
        string line = SignalWireCodec.FormatLine(values, template);

        string[] parts = line.Split(',');
        Assert.Equal(32, parts.Length);
        // First telemetry field = variable index 2 (SlurryDensity/Recirc Density) with F2.
        Assert.Equal("2.00", parts[0]);
        // Last telemetry field = variable index 33 (VolumetricMeter) with F2.
        Assert.Equal("33.00", parts[^1]);
        // Excluded columns must not leak onto the wire.
        Assert.DoesNotContain(parts, p => p == "0.0");   // elapsed would be F1 "0.0"
        Assert.DoesNotContain(parts, p => p == "1");     // Unit Num int
    }

    [Fact]
    public void TemplateWithoutExclusions_EmitsAllFields()
    {
        var template = new DeviceTemplate
        {
            Name = "mini",
            DeviceType = "PLC",
            Variables = new[]
            {
                new TemplateVariable { Index = 0, Name = "A", Format = "" },
                new TemplateVariable { Index = 1, Name = "B", Format = "int" },
                new TemplateVariable { Index = 2, Name = "C", Format = "elapsed" },
            },
        };

        string line = SignalWireCodec.FormatLine(new[] { 1.0, 2.6, 4.1 }, template);

        Assert.Equal("1.00,3,4.1", line);   // F2, int(Math.Round(2.6))=3, F1
    }

    [Fact]
    public void PlcBasic_FromCatalog_OmitsPlcTimestamp()
    {
        var templates = TemplateCatalog.Load();
        var template = templates.FirstOrDefault(t =>
            string.Equals(t.DeviceType, "PLC", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("PLC template not found in catalog");

        var values = Enumerable.Range(0, template.Variables.Length).Select(i => (double)i).ToArray();
        string line = SignalWireCodec.FormatLine(values, template);

        // plc_basic.json: 10 variables incl. "PLC Timestamp" (excludeFromTelemetry) ⇒ 9 on wire.
        string[] parts = line.Split(',');
        Assert.Equal(9, parts.Length);
        Assert.Equal("1", parts[0]);   // PLC Status at index 1 (registry format "int")
    }
}
