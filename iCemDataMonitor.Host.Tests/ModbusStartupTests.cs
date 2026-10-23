using System.Net.Sockets;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace iCemDataMonitor.Host.Tests;

public sealed class ModbusStartupTests
{
    private sealed class Latest : ILatestBusState
    {
        public LatestBusState Current { get; } = new(null, false, null, null, null, null, 0);
    }
    private sealed class Publisher : IModbusPublisher
    {
        internal int Starts, Stops;
        internal Func<CancellationToken, Task> Start = _ => Task.CompletedTask;
        public bool IsBound { get; private set; }
        public async Task StartAsync(CancellationToken ct = default) { Starts++; await Start(ct); IsBound = true; }
        public Task StopAsync() { Stops++; IsBound = false; return Task.CompletedTask; }
        public Task ConfigureAsync(IReadOnlyList<ModbusRegisterEntry> map) => Task.CompletedTask;
    }

    private static ModbusBindException Busy() => new(1502, new SocketException((int)SocketError.AddressAlreadyInUse));

    [Fact]
    public async Task Busy_HasFiveAttemptsFourFiveSecondDelaysAndOriginalCause()
    {
        var publisher = new Publisher { Start = _ => throw Busy() };
        var delays = new List<TimeSpan>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostComposition.StartModbusWithPortRetryAsync(publisher, 1502, _ => { }, default,
                (duration, _) => { delays.Add(duration); return Task.CompletedTask; }));
        Assert.Equal(5, publisher.Starts);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(5), 4), delays);
        Assert.IsType<ModbusBindException>(error.InnerException);
        Assert.Contains("5 attempt(s)", error.Message);
    }

    [Theory]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.AddressNotAvailable)]
    public async Task OtherSocketFailures_AreNotRetried(SocketError code)
    {
        var publisher = new Publisher { Start = _ => throw new ModbusBindException(1502, new SocketException((int)code)) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostComposition.StartModbusWithPortRetryAsync(
            publisher, 1502, _ => { }, default, (_, _) => throw new Xunit.Sdk.XunitException("Unexpected delay")));
        Assert.Equal(1, publisher.Starts);
        Assert.Contains("1 attempt(s)", error.Message);
    }

    [Fact]
    public async Task MessageHeuristicAndArgumentFailure_AreNotRetried()
    {
        foreach (var cause in new Exception[] { new ArgumentException("port address"), new IOException("EADDRINUSE") })
        {
            var publisher = new Publisher { Start = _ => throw cause };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostComposition.StartModbusWithPortRetryAsync(publisher, 1502, _ => { }, default));
            Assert.Same(cause, error.InnerException);
            Assert.Equal(1, publisher.Starts);
        }
    }

    [Fact]
    public async Task Stopping_CancelsBusyDelayWithoutAnotherAttempt()
    {
        using var stopping = new CancellationTokenSource();
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher { Start = token =>
        {
            Assert.Equal(stopping.Token, token);
            throw Busy();
        } };
        var startup = HostComposition.StartModbusWithPortRetryAsync(publisher, 1502, _ => { }, stopping.Token,
            (_, token) => { enteredDelay.SetResult(); return Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(1, publisher.Starts);
    }

    [Fact]
    public async Task StartIsAwaited_AndSuccessEndsRetry()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher();
        publisher.Start = _ => publisher.Starts < 3 ? throw Busy() : completion.Task;
        int delays = 0;
        var startup = HostComposition.StartModbusWithPortRetryAsync(publisher, 1502, _ => { }, default,
            (_, _) => { delays++; return Task.CompletedTask; });
        Assert.False(startup.IsCompleted);
        completion.SetResult();
        await startup;
        Assert.Equal(3, publisher.Starts);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task BootAndRuntimeActuator_UseSameRegisteredSingleton_RuntimeHasOneAttempt()
    {
        var publisher = new Publisher();
        var services = new ServiceCollection();
        services.AddSingleton<IModbusPublisher>(publisher);
        services.AddSingleton(new IniOptions());
        using var provider = services.BuildServiceProvider();
        await HostComposition.StartLayersAsync(provider, new LayerFlags(true, false, false, false), _ => { });
        var actuator = new DefaultLayerActuator(provider.GetRequiredService<IModbusPublisher>(), null);
        await actuator.StopModbusAsync();
        Assert.Equal(1, publisher.Starts);
        Assert.Equal(1, publisher.Stops);
        publisher.Start = _ => throw Busy();
        await Assert.ThrowsAsync<ModbusBindException>(() => actuator.StartModbusAsync());
        Assert.Equal(2, publisher.Starts);
    }

    [Fact]
    public async Task ProductionComposition_BootAndDiRuntimeActuatorOperateSamePublisher()
    {
        var services = new ServiceCollection();
        var ini = new IniOptions { ModbusPort = 0 };
        var layers = new LayerFlags(true, false, false, false);
        HostComposition.RegisterCoreServices(services, ini, layers, null, _ => { });
        HostComposition.RegisterLayerController(services, ini, layers, "unused.ini");
        // No MMF I/O is needed to verify production registrations and listener identity.
        services.AddSingleton<ILatestBusState>(new Latest());
        using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IModbusPublisher>();
        Assert.IsType<ModbusTcpPublisher>(publisher);
        await HostComposition.StartLayersAsync(provider, layers, _ => { });
        Assert.True(publisher.IsBound);
        var actuator = provider.GetRequiredService<LayerRuntimeController.ILayerActuator>();
        await actuator.StopModbusAsync();
        Assert.False(publisher.IsBound);
        await actuator.StartModbusAsync();
        Assert.True(publisher.IsBound);
        Assert.Same(publisher, provider.GetRequiredService<IModbusPublisher>());
    }

    [Fact]
    public async Task FailureDiagnostics_ReportActualBindPort()
    {
        var publisher = new Publisher { Start = _ => throw new ModbusBindException(3333,
            new SocketException((int)SocketError.AccessDenied)) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostComposition.StartModbusWithPortRetryAsync(publisher, 2222, _ => { }, default));
        Assert.Contains("port 3333", error.Message);
    }
}
