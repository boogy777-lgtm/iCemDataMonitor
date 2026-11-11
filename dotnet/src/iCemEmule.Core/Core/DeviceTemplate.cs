// CONTRACT: Halliburton.iCemEmule.Core — DeviceTemplate, TemplateVariable, ISimulationEngine, IAcePayloadProvider, AceVectorData
// Frozen per CONTRACT_iCemEmule.yaml. Breaking changes = STOP + escalation.

namespace Halliburton.iCemEmule.Core;

public sealed class DeviceTemplate
{
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public int[] AutoSelectIndices { get; set; } = Array.Empty<int>();
    public TemplateVariable[] Variables { get; set; } = Array.Empty<TemplateVariable>();
    public int FieldCount => Variables.Length;
}

public sealed class TemplateVariable
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LegacyName { get; set; } = string.Empty;
    public string UoM { get; set; } = string.Empty;
    public double BaseValue { get; set; }
    public double Amplitude { get; set; }
    public double Frequency { get; set; }
    public string Format { get; set; } = string.Empty;   // "elapsed" | "int" | "" (=>F2)
    public double TogglePeriod { get; set; }              // >0 ⇒ digital toggle 0/1
    public bool ExcludeFromTelemetry { get; set; }
}

public interface ISimulationEngine
{
    IReadOnlyList<DeviceTemplate> Templates { get; }
    DeviceTemplate? Current { get; }
    void SetTemplate(string deviceType, string templateName);
    double[] Tick();
    void ResetClock();
    event Action? TemplateSwapped;
}

public sealed record AceVectorData(uint SvNumber, string Name, byte[] Payload);  // Payload = 124 bytes

public interface IAcePayloadProvider
{
    IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds);
    IReadOnlyList<uint> ActiveSvNumbers { get; }
}
