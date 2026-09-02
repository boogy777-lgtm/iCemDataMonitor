using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

public interface IConfigStore
{
    IReadOnlyList<DeviceFamily> GetAllFamilies();
    DeviceConfig? GetActiveConfig();
    IReadOnlyList<VariableDefinition> GetActiveVariables();
    Task ActivateConfigAsync(string family, string configName);
    IReadOnlyList<ModbusRegisterEntry> GetModbusRegisterMap();
    /// <summary>
    /// Feeds live variable names discovered from the data flow (NOT from MongoDB directly).
    /// Called by PipelineWorker after first data batches arrive through the Subscribe callback.
    /// Populates the name set used by the 3-pass merge in RebuildAsync.
    /// </summary>
    Task RefreshFromLiveAsync(IEnumerable<string> liveVariableNames);
}
