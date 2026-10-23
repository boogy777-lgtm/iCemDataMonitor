using System.Net;
using System.Net.Sockets;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport.Tests;

public sealed class ModbusLifecycleTests
{
    private sealed class Absent : ILatestBusState
    {
        public LatestBusState Current { get; } = new(null, false, null, null, null, null, 0);
    }

    private static ModbusTcpPublisher Create(ModbusOptions? options = null) =>
        new(new Absent(), options ?? new ModbusOptions { Port = 0 }, TimeSpan.FromHours(1));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task RepeatedConcurrentLifecycle_RebindsAndPreservesRegisterImage()
    {
        var options = new ModbusOptions { Port = 0, Total = 650 };
        using var publisher = Create(options);
        await publisher.StopAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => publisher.StartAsync()));
        var port = publisher.BoundPort;
        Assert.True(publisher.IsBound);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            // FC06 reserve write; response confirms this client was accepted and processed.
            byte[] write = [0, 1, 0, 0, 0, 6, 1, 6, 2, 88, 0x12, 0x34];
            await client.GetStream().WriteAsync(write);
            var response = new byte[12];
            await client.GetStream().ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(write, response);
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => publisher.StopAsync()));
        Assert.False(publisher.IsBound);
        options.Port = port;
        await publisher.StartAsync();
        Assert.Equal(port, publisher.BoundPort);
        using var reader = new TcpClient();
        await reader.ConnectAsync(IPAddress.Loopback, port);
        await reader.GetStream().WriteAsync(new byte[] { 0, 2, 0, 0, 0, 6, 1, 3, 2, 88, 0, 1 });
        var read = new byte[11];
        await reader.GetStream().ReadExactlyAsync(read).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new byte[] { 0x12, 0x34 }, read[9..]);
    }

    [Fact]
    public async Task StopAndDispose_JoinClientsBlockedInHeaderAndBody_ThenAllowRebind()
    {
        var publisher = Create();
        try
        {
            await publisher.StartAsync();
            var port = publisher.BoundPort;
            using var header = new TcpClient();
            using var body = new TcpClient();
            await header.ConnectAsync(IPAddress.Loopback, port);
            await body.ConnectAsync(IPAddress.Loopback, port);
            await body.GetStream().WriteAsync(new byte[] { 0, 1, 0, 0, 0, 6 });
            await WaitForAsync(() => publisher.ActiveClientCount == 2);
            await publisher.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, publisher.ActiveClientCount);
            Assert.Equal(0, await header.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, await body.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            using var replacement = new TcpListener(IPAddress.Any, port);
            replacement.Start();
            replacement.Stop();
            await publisher.StartAsync();
            using var blocked = new TcpClient();
            await blocked.ConnectAsync(IPAddress.Loopback, publisher.BoundPort);
            await WaitForAsync(() => publisher.ActiveClientCount == 1);
            publisher.Dispose();
            Assert.False(publisher.IsBound);
            Assert.Equal(0, await blocked.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            await publisher.StopAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => publisher.StartAsync());
        }
        finally { publisher.Dispose(); }
    }

    [Fact]
    public async Task BusyBindAndCancellation_LeaveSameInstanceRestartable()
    {
        using var occupied = new TcpListener(IPAddress.Any, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using var publisher = Create(new ModbusOptions { Port = port });
        var failure = await Assert.ThrowsAsync<ModbusBindException>(() => publisher.StartAsync());
        Assert.Equal(port, failure.Port);
        Assert.Equal(SocketError.AddressAlreadyInUse, failure.SocketErrorCode);
        Assert.IsType<SocketException>(failure.InnerException);
        Assert.False(publisher.IsBound);
        occupied.Stop();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.StartAsync(cancelled.Token));
        await publisher.StartAsync();
        Assert.True(publisher.IsBound);
    }

    [Fact]
    public async Task CompletedClients_AreRemovedDuringLongRunningGeneration()
    {
        using var publisher = Create();
        await publisher.StartAsync();
        for (var i = 0; i < 30; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, publisher.BoundPort);
            await WaitForAsync(() => publisher.ActiveClientCount == 1);
            client.Close();
            await WaitForAsync(() => publisher.ActiveClientCount == 0);
        }
    }
}
