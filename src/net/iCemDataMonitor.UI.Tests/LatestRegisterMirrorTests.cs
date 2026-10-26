using System.Net;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Host.Core.Endpoints;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace iCemDataMonitor.UI.Tests;

public sealed class LatestRegisterMirrorTests
{
    private sealed class Latest : ILatestBusState
    {
        public int Reads;
        public LatestBusState State = new(null, false, "first capture pending", null, null, null, 0);
        public LatestBusState Current { get { Reads++; return State; } }
    }

    [Fact]
    public async Task Request_OnlyReadsOneCurrent_NoReaderRegistered_FirstAndClosed503_KnownOpenStaleAvailable()
    {
        var latest = new Latest();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILatestBusState>(latest);
        await using var app = builder.Build();
        app.MapRegisterMirror();
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/regs")).StatusCode);
        Assert.Equal(1, latest.Reads);
        var snapshot = new BusSnapshot(BusLayout.Magic, BusLayout.Version, BusLayout.LayoutVersion, 600,
            new ushort[600], default, default, default, default, true, 0);
        latest.State = new(snapshot, true, "timeout", 0, 0, 0, 1);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/regs")).StatusCode);
        Assert.Equal(2, latest.Reads);
        latest.State = latest.State with { IsOpen = false };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/regs")).StatusCode);
        Assert.Equal(3, latest.Reads);
        await app.StopAsync();
    }
}
