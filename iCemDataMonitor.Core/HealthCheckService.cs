using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Core.Models;
using iCemDataMonitor.Sdk;

namespace iCemDataMonitor.Core;

/// <summary>
/// Health probe: every N seconds pings MongoDB via IDataSource.DiscoverAsync (canary)
/// and checks Windows process "FlecsTray.exe" or service "iCem Lan Streamer".
/// Publishes HealthEvent to Channel — Core never inspects or routes payload.
/// Windows-only by design: ServiceController + process checks (the POC hosts are
/// Windows services/desktop apps — debt_6b: attribute, ¬ suppression).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HealthCheckService : IHostedService, IDisposable
{
    private readonly IDataSource _dataSource;
    private readonly Channel<HealthEvent> _healthChannel;
    private readonly CoreOptions _options;
    private readonly ILogger<HealthCheckService> _logger;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public HealthCheckService(
        IDataSource dataSource,
        Channel<HealthEvent> healthChannel,
        CoreOptions options,
        ILogger<HealthCheckService> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _healthChannel = healthChannel ?? throw new ArgumentNullException(nameof(healthChannel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[health] Starting health check loop every {Interval}s",
            _options.HealthCheckIntervalSeconds);

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds));
        _loopTask = RunLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[health] Stopping health check loop");
        _cts?.Cancel();

        if (_loopTask != null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }

        _timer?.Dispose();
        _cts?.Dispose();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                var dbOk = await CheckMongoDbAsync();
                var icemOk = CheckIcemProcess();

                _healthChannel.Writer.TryWrite(new HealthEvent(DateTime.UtcNow, dbOk, icemOk));

                _logger.LogDebug(
                    "[health] Probe: DB={DbOk}, iCem={IcemOk}",
                    dbOk ? "UP" : "DOWN", icemOk ? "UP" : "DOWN");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[health] Probe iteration failed");
            }
        }
    }

    /// <summary>
    /// Canary: IDataSource.DiscoverAsync succeeds ⇒ MongoDB reachable.
    /// </summary>
    private async Task<bool> CheckMongoDbAsync()
    {
        try
        {
            await _dataSource.DiscoverAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks for the legacy "iCem.exe" desktop process (exact name) OR the
    /// "iCem Lan Streamer" Windows service. Exact match required — emulator
    /// processes (FlecsTray*.exe) MUST NOT count as iCem health (test flake 2026-08).
    /// </summary>
    private static bool CheckIcemProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("iCem");
            if (processes.Length > 0)
                return true;
        }
        catch { /* process list access denied — fall through */ }

        try
        {
            using var sc = new ServiceController("iCem Lan Streamer");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { /* service not installed / access denied */ }

        return false;
    }
}
