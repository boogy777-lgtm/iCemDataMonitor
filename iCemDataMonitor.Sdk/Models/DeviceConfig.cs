namespace iCemDataMonitor.Sdk.Models;

public readonly record struct DeviceConfig(
    string Name,
    IReadOnlyList<VariableDefinition> Variables
);
