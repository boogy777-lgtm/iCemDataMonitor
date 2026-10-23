using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

public sealed class WindowsNamedEventFactAttribute : FactAttribute
{
    public WindowsNamedEventFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows native named events";
    }
}

public sealed class HostCommandListenersTests
{
    private static string Name() => $"Local\\icem-c8-{Guid.NewGuid():N}";

    [WindowsNamedEventFact]
    public async Task BothWorkers_DispatchAndJoinBeforeHandlesDispose_AcrossRepeatedLifetimes()
    {
        var restartName = Name();
        var shutdownName = Name();
        for (int i = 0; i < 3; i++)
        {
            using var stopping = new CancellationTokenSource();
            var restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new HostCommandListeners(restartName, shutdownName, stopping.Token,
                () => restart.TrySetResult(), () => close.TrySetResult());
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(restartName)) signal.Set();
                using (var signal = EventWaitHandle.OpenExisting(shutdownName)) signal.Set();
                await Task.WhenAll(restart.Task, close.Task).WaitAsync(TimeSpan.FromSeconds(30));
                stopping.Cancel();
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                owner.Dispose();
                owner.Dispose();
                Assert.True(owner.Completion.IsCompletedSuccessfully);
                Assert.Throws<WaitHandleCannotBeOpenedException>(() => EventWaitHandle.OpenExisting(restartName));
                Assert.Throws<WaitHandleCannotBeOpenedException>(() => EventWaitHandle.OpenExisting(shutdownName));
            }
            finally { owner.Dispose(); }
        }
    }

    [WindowsNamedEventFact]
    public async Task Dispose_WaitsForBothCallbacksBeforeReleasingTheirSharedResources()
    {
        using var release = new ManualResetEventSlim();
        var entered = new CountdownEvent(2);
        var restartName = Name();
        var shutdownName = Name();
        var owner = new HostCommandListeners(restartName, shutdownName, default,
            () => { entered.Signal(); release.Wait(); }, () => { entered.Signal(); release.Wait(); });
        Task? disposing = null;
        try
        {
            using (var signal = EventWaitHandle.OpenExisting(restartName)) signal.Set();
            using (var signal = EventWaitHandle.OpenExisting(shutdownName)) signal.Set();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposing = Task.Factory.StartNew(() =>
            {
                disposeEntered.SetResult();
                owner.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(owner.Completion.IsCompleted);
            using (EventWaitHandle.OpenExisting(restartName)) { }
            using (EventWaitHandle.OpenExisting(shutdownName)) { }
        }
        finally
        {
            release.Set();
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(30));
            owner.Dispose();
            entered.Dispose();
        }
        Assert.True(owner.Completion.IsCompletedSuccessfully);
    }

    [WindowsNamedEventFact]
    public void PartialStartFailure_ReleasesFirstEventAndStopRegistration()
    {
        var restartName = Name();
        var shutdownName = Name();
        using var incompatible = new Mutex(false, shutdownName);
        using var stopping = new CancellationTokenSource();
        Assert.ThrowsAny<WaitHandleCannotBeOpenedException>(() => new HostCommandListeners(
            restartName, shutdownName, stopping.Token, () => { }, () => { }));
        stopping.Cancel();
        Assert.Throws<WaitHandleCannotBeOpenedException>(() => EventWaitHandle.OpenExisting(restartName));
    }

    [WindowsNamedEventFact]
    public async Task StaleSignalsAreReset_AndAlreadyStoppingDoesNotDispatch()
    {
        var restartName = Name();
        var shutdownName = Name();
        using var restart = new EventWaitHandle(true, EventResetMode.AutoReset, restartName);
        using var shutdown = new EventWaitHandle(true, EventResetMode.AutoReset, shutdownName);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        int commands = 0;
        using var owner = new HostCommandListeners(restartName, shutdownName, stopping.Token,
            () => Interlocked.Increment(ref commands), () => Interlocked.Increment(ref commands));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, commands);
        Assert.False(restart.WaitOne(0));
        Assert.False(shutdown.WaitOne(0));
    }

    [WindowsNamedEventFact]
    public async Task CallbackFailure_StillJoinsOtherWorkerAndReleasesHandles()
    {
        var restartName = Name();
        var shutdownName = Name();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new HostCommandListeners(restartName, shutdownName, default,
            () => { entered.TrySetResult(); throw new InvalidOperationException("dispatch failed"); }, () => { });
        using (var signal = EventWaitHandle.OpenExisting(restartName)) signal.Set();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        Assert.True(owner.Completion.IsCompleted);
        Assert.Throws<WaitHandleCannotBeOpenedException>(() => EventWaitHandle.OpenExisting(restartName));
        Assert.Throws<WaitHandleCannotBeOpenedException>(() => EventWaitHandle.OpenExisting(shutdownName));
        owner.Dispose();
    }
}
