namespace iCemDataMonitor.Sdk.Models;

public readonly record struct DeviceFamily(
    string Name,
    IReadOnlyList<DeviceConfig> Configs
);
