using Grpc.Core;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.HostClient;
using Moq;
using Xunit;

namespace iCemDataMonitor.UI.Tests;

public sealed class UiThemeCoordinatorTests
{
    private readonly Mock<IHostApi> _api = new();
    private readonly List<bool> _applied = new();
    private readonly List<string> _logs = new();
    private bool _local;
    private bool _connected = true;
    private int _writes;
    private UiThemeCoordinator Create()
    {
        _api.Setup(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiPrefs());
        return new(() => _local, light => { _writes++; return _local = light; }, _applied.Add, _logs.Add);
    }

    [Fact]
    public async Task DifferentReplyWinsWithoutLocalRetryOrSavedClaim()
    {
        using var c = Create();
        await c.AttachAsync(_api.Object, () => _connected);
        _api.Setup(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(new UiPrefs());
        await c.ToggleAsync(true);
        Assert.False(_applied.Last());
        Assert.Equal(0, _writes);
        Assert.DoesNotContain(_logs, x => x.Contains("persisted"));
    }

    [Fact]
    public async Task OfflineBeforeSendWritesLocallyThenReconnectAdoptsHostWithoutReplay()
    {
        using var c = Create();
        _connected = false;
        await c.AttachAsync(_api.Object, () => _connected);
        await c.ToggleAsync(true);
        Assert.True(_applied.Last());
        Assert.Equal(1, _writes);
        _connected = true;
        await c.RefreshAsync();
        Assert.False(_applied.Last());
        Assert.Equal(1, _writes);
        _api.Verify(x => x.SetUiThemeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _api.Verify(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task AttemptedRpcFailureReconcilesWithoutSecondWriter(StatusCode code)
    {
        using var c = Create();
        await c.AttachAsync(_api.Object, () => _connected);
        _api.Setup(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(code, "fault after send")));
        await c.ToggleAsync(true);
        Assert.Equal(0, _writes);
        Assert.False(_applied.Last());
        Assert.Contains(_logs, x => x.Contains("unconfirmed"));
    }

    [Fact]
    public async Task HealthyHostWithClosedBusUsesControlRpc()
    {
        // Pipe liveness is true independently of the metrics/BusOpen state.
        using var c = Create();
        _api.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HostStatus { BusOpen = false });
        _api.Setup(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(new UiPrefs { ThemeLight = true });
        await c.AttachAsync(_api.Object, () => true);
        await c.ToggleAsync(true);
        Assert.True(_applied.Last());
        Assert.Equal(0, _writes);
        _api.Verify(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitialAttachAndRefreshReadHostAndListenerEchoNeverPersists()
    {
        using var c = Create();
        _api.Setup(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UiPrefs { ThemeLight = true });
        await c.AttachAsync(_api.Object, () => true);
        Assert.True(_applied.Last());
        await c.RefreshAsync();
        _local = true;
        await c.ReadLocalAsync();
        Assert.True(_applied.Last());
        Assert.Equal(0, _writes);
        _api.Verify(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _api.Verify(x => x.SetUiThemeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessionOrClosedDelayedReplyCannotApply(bool close)
    {
        using var c = Create();
        await c.AttachAsync(_api.Object, () => true);
        var pending = new TaskCompletionSource<UiPrefs>();
        _api.Setup(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var old = c.ToggleAsync(true);
        if (close) c.Dispose();
        else await c.AttachAsync(null, () => false);
        var count = _applied.Count;
        pending.SetResult(new UiPrefs { ThemeLight = true });
        await old;
        Assert.Equal(count, _applied.Count);
        Assert.False(_applied.Last());
    }

    [Fact]
    public async Task ConcurrentCommandsAreOrderedAndListenerReadsAfterQueuedCommand()
    {
        using var c = Create();
        await c.AttachAsync(_api.Object, () => true);
        var pending = new TaskCompletionSource<UiPrefs>();
        var calls = new List<bool>();
        _api.Setup(x => x.SetUiThemeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((bool light, CancellationToken _) =>
            {
                calls.Add(light);
                return light ? pending.Task : Task.FromResult(new UiPrefs());
            });
        var first = c.ToggleAsync(true);
        var second = c.ToggleAsync(false);
        var echo = c.ReadLocalAsync();
        Assert.Equal(new[] { true }, calls);
        _local = false;
        pending.SetResult(new UiPrefs { ThemeLight = true });
        await Task.WhenAll(first, second, echo);
        Assert.Equal(new[] { true, false }, calls);
        Assert.Equal(new[] { false, true, false, false }, _applied);
        Assert.Equal(0, _writes);
    }
    [Fact]
    public async Task ReentrantApplyKeepsReservationAndFaultDoesNotPoisonQueue()
    {
        UiThemeCoordinator? c = null;
        Task? reentrant = null;
        var applied = new List<bool>();
        var first = true;
        c = new UiThemeCoordinator(() => false, x => x, light =>
        {
            applied.Add(light);
            if (first)
            {
                first = false;
                reentrant = c!.ToggleAsync(false);
                throw new InvalidOperationException("consumer fault");
            }
        }, _ => { });
        using (c)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.ToggleAsync(true));
            await reentrant!;
            await c.ToggleAsync(true);
            Assert.Equal(new[] { true, false, true }, applied);
        }
    }

    [Fact]
    public async Task ReconnectWithStaleDownResetToUnknownReadsHostWithoutReplay()
    {
        using var c = Create();
        bool? pipeAlive = false;
        await c.AttachAsync(_api.Object, () => pipeAlive != false);
        await c.ToggleAsync(true);
        Assert.Equal(1, _writes);
        pipeAlive = null; // Shell Connected invalidates the previous Down probe
        await c.RefreshAsync();
        Assert.False(_applied.Last());
        Assert.Equal(1, _writes);
        _api.Verify(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(x => x.SetUiThemeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstConfirmedAliveRefreshRecoversFailedInitialAttachWithoutIntentReplay()
    {
        using var c = Create();
        bool? pipeAlive = null;
        _api.SetupSequence(x => x.GetUiPrefsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "starting")))
            .ReturnsAsync(new UiPrefs { ThemeLight = true });
        await c.AttachAsync(_api.Object, () => pipeAlive != false);
        Assert.False(_applied.Last());
        var previous = pipeAlive;
        pipeAlive = true; // first pipe response; metrics may remain Lost/closed
        if (pipeAlive == true && previous != true) await c.RefreshAsync();
        Assert.True(_applied.Last());
        Assert.Equal(0, _writes);
        _api.Verify(x => x.SetUiThemeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        public readonly Queue<(SendOrPostCallback Callback, object? State)> Work = new();
        public override void Post(SendOrPostCallback callback, object? state) => Work.Enqueue((callback, state));
        public void Drain()
        {
            while (Work.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    [Fact]
    public async Task AlreadyPostedUiContinuationCannotApplyAfterSessionReplacement()
    {
        using var c = Create();
        await c.AttachAsync(_api.Object, () => true);
        var pending = new TaskCompletionSource<UiPrefs>();
        _api.Setup(x => x.SetUiThemeAsync(true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var ui = new QueuedContext();
        var original = SynchronizationContext.Current;
        Task old;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            old = c.ToggleAsync(true);
            SynchronizationContext.SetSynchronizationContext(null);
            pending.SetResult(new UiPrefs { ThemeLight = true });
            Assert.NotEmpty(ui.Work);
            await c.AttachAsync(null, () => false);
            var count = _applied.Count;
            ui.Drain();
            await old;
            Assert.Equal(count, _applied.Count);
            Assert.False(_applied.Last());
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
    }

}
