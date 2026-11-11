using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

public interface IDataSource
{
    Task<IReadOnlyList<VariableDefinition>> DiscoverAsync();
    void Subscribe(Action<DateTime, IReadOnlyDictionary<string, double>> handler);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
