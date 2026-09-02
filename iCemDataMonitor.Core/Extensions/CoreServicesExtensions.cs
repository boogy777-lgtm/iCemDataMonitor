using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using iCemDataMonitor.Core.Models;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Core.Extensions;

/// <summary>
/// v1 pipeline DI wiring (PipelineWorker + HealthCheckService). Not currently
/// called by any host — retained with its v1 pipeline tests. Windows-only
/// because HealthCheckService is (debt_6b propagation: attribute on the
/// consumer, ¬ a suppression).
/// </summary>
[SupportedOSPlatform("windows")]
public static class CoreServicesExtensions
{
    public static IServiceCollection AddCorePipeline(this IServiceCollection services)
    {
        services.AddSingleton(Channel.CreateUnbounded<TimeSeriesSample>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));

        services.AddSingleton(Channel.CreateUnbounded<HealthEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true }));

        services.AddHostedService<PipelineWorker>();
        services.AddHostedService<HealthCheckService>();

        return services;
    }
}
