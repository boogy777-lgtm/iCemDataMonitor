using System.Reflection;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;
using Xunit;

namespace iCemEmule.Tests;

[Collection("EmulatorControlStatics")]
public sealed class EngineShutdownTests
{
    private sealed class Com : IComEmitter
    {
        public bool IsOpen { get; private set; }
        public string? PortName => "fake";
        public long LinesSent { get; private set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        internal int Closes, Disposals;
        internal bool Block;
        internal readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public string Open(string name) { IsOpen = true; return ""; }
        public void SendLine(string line)
        {
            LinesSent++;
            if (Block) { Entered.Set(); Assert.True(Release.Wait(TimeSpan.FromSeconds(15))); }
        }
        public void Close() { Closes++; IsOpen = false; }
        public void Dispose() { Disposals++; }
    }

    private sealed class Lan : IAceLanEmitter
    {
        public bool IsOpen { get; private set; }
        public string? AdapterName => "fake";
        public long FramesSent { get; private set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        internal readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        internal int Closes, Disposals;
        internal Func<string>? CommandDuringSend;
        internal string? Reply;
        public string Open(string name) { IsOpen = true; return ""; }
        public void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac)
        {
            Entered.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(15)));
            Reply = CommandDuringSend?.Invoke(); // completion may need the host gate
            FramesSent++;
        }
        public void Close() { Closes++; IsOpen = false; }
        public void Dispose() { Disposals++; }
    }

    private static bool Stopping(EmuEngineHost host)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(EmuEngineHost).GetField("_gate", flags)!.GetValue(host)!;
        lock (gate) return (bool)typeof(EmuEngineHost).GetField("_stopping", flags)!.GetValue(host)!;
    }

    [Fact]
    public async Task StopJoinsBlockedLanOutsideGate_ConcurrentStopHasOneResourceOwner()
    {
        var previous = EmuSettings.SettingsPathOverride;
        var path = Path.Combine(Path.GetTempPath(), "c9_stop_" + Guid.NewGuid().ToString("N"), "settings.xml");
        EmuSettings.SettingsPathOverride = path;
        var com = new Com();
        var lan = new Lan();
        var host = new EmuEngineHost(new EmuSettings { LanEnabled = true, LanAdapter = "fake" }, com, lan,
            "c9_stop_" + Guid.NewGuid().ToString("N"));
        lan.CommandDuringSend = () => host.Execute("status").Json;
        try
        {
            host.Start();
            Assert.True(lan.Entered.Wait(TimeSpan.FromSeconds(10)));
            var first = Task.Run(host.Stop);
            Assert.True(SpinWait.SpinUntil(() => Stopping(host), TimeSpan.FromSeconds(10)));
            var second = Task.Run(host.Stop);
            Assert.False(first.IsCompleted);
            Assert.Equal(0, lan.Closes);
            Assert.Equal(0, lan.Disposals);
            Assert.Equal("engine is stopping", host.StartChannel("lan").Error);
            lan.Release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(lan.Reply);
            Assert.Equal(1, lan.Closes);
            Assert.Equal(1, lan.Disposals);
            Assert.Equal(1, com.Closes);
            Assert.Equal(1, com.Disposals);
            host.Stop();
            host.Start(); // stopped host cannot reactivate timer/pipe/emitters
            Assert.Equal(1, lan.Closes);
            Assert.Equal(1, lan.FramesSent);
        }
        finally
        {
            lan.Release.Set(); host.Stop(); EmuSettings.SettingsPathOverride = previous;
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    [Fact]
    public async Task StopWaitsForInflightTimerComSend_BeforeClosingEmitters()
    {
        var previous = EmuSettings.SettingsPathOverride;
        var path = Path.Combine(Path.GetTempPath(), "c9_timer_" + Guid.NewGuid().ToString("N"), "settings.xml");
        EmuSettings.SettingsPathOverride = path;
        var com = new Com { Block = true };
        var lan = new Lan();
        var host = new EmuEngineHost(new EmuSettings { ComEnabled = true, ComPort = "fake" }, com, lan,
            "c9_timer_" + Guid.NewGuid().ToString("N"));
        try
        {
            host.Start();
            Assert.True(com.Entered.Wait(TimeSpan.FromSeconds(10)));
            var enteredStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = Task.Run(() => { enteredStop.SetResult(); host.Stop(); });
            await enteredStop.Task;
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, com.Closes);
            com.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, com.Closes);
            Assert.Equal(1, com.Disposals);
        }
        finally
        {
            com.Release.Set(); host.Stop(); EmuSettings.SettingsPathOverride = previous;
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }
}
