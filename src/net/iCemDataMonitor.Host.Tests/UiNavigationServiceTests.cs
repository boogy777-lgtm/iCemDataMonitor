using iCemDataMonitor.Contracts;
using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

// W21.3 unit coverage of the pending-navigation coordinator. Real named
// mutex/event handles are used but with UNIQUE per-test names (ctor-injected),
// so tests never collide with a live shell or with each other.
public class UiNavigationServiceTests
{
    private static (string MutexName, string EventName) UniqueNames() =>
        ($@"Local\test_nav_mutex_{Guid.NewGuid():N}",
         $@"Local\test_nav_event_{Guid.NewGuid():N}");

    // These tests synchronously probe an OS mutex. Their holder must not depend
    // on ThreadPool availability, or on a fixed sleep outlasting the test.
    private sealed class HeldMutex : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private Exception? _failure;
        private bool _disposed;

        public HeldMutex(string name)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(false, name);
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("navigation fixture mutex acquisition timed out");
                    try
                    {
                        _ready.Set();
                        if (!_release.Wait(TimeSpan.FromSeconds(30)))
                            throw new TimeoutException("navigation fixture release was not requested");
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                catch (Exception error)
                {
                    _failure = error;
                    _ready.Set();
                }
            }) { IsBackground = true, Name = "navigation-test-mutex" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _failure is not null)
            {
                Dispose();
                throw new InvalidOperationException("holder thread failed to take the mutex", _failure);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "navigation fixture holder did not stop");
            _disposed = true;
            _ready.Dispose();
            _release.Dispose();
            Assert.Null(_failure);
        }
    }

    [Fact]
    public void HeldMutex_OwnsOnDedicatedThread_AndReleasesBeforeDisposeReturns()
    {
        var (name, _) = UniqueNames();
        Assert.False(MutexProbe.IsTaken(name));
        using (var holder = new HeldMutex(name))
            Assert.True(MutexProbe.IsTaken(name));
        Assert.False(MutexProbe.IsTaken(name));
    }

    [Fact]
    public void Navigate_DeadUi_StoresPending_ReportsHonestAliveFalse()
    {
        var (m, e) = UniqueNames();
        var nav = new UiNavigationService(m, e);

        var r = nav.Navigate(UiSection.Status);

        Assert.False(r.UiAlive);                    // nobody holds the mutex
        Assert.Equal(UiSection.Status, r.Pending);
        // Pending survives for the next shell show…
        Assert.Equal(UiSection.Status, nav.TakePending());
        // …and is consumed once (read-and-clear).
        Assert.Equal(UiSection.Unspecified, nav.TakePending());
    }

    [Fact]
    public void Navigate_AliveUi_SignalsShowEvent()
    {
        var (m, e) = UniqueNames();
        // A mutex is REENTRANT per thread — if THIS test thread owned it,
        // MutexProbe would see "free". Hold it from another thread instead.
        using var holder = new HeldMutex(m);
        {
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, e);
            var nav = new UiNavigationService(m, e);

            // 2026-08-27: only UiSection.Status remains (web/emulator reserved).
            var r = nav.Navigate(UiSection.Status);

            Assert.True(r.UiAlive);
            Assert.Equal(UiSection.Status, r.Pending);
            Assert.True(showEvent.WaitOne(TimeSpan.Zero)); // the shell wake-up signal fired
            Assert.Equal(UiSection.Status, nav.TakePending()); // still stored for late consumers
        }
    }

    [Fact]
    public void Navigate_Unspecified_IsQueryArm_DoesNotSignal()
    {
        var (m, e) = UniqueNames();
        using var holder = new HeldMutex(m);
        {
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, e);
            var nav = new UiNavigationService(m, e);
            nav.Navigate(UiSection.Status); // store pending (shell "alive" here)
            Assert.True(showEvent.WaitOne(TimeSpan.Zero)); // drain the SETUP signal

            var q = nav.Navigate(UiSection.Unspecified);

            Assert.True(q.UiAlive);
            Assert.Equal(UiSection.Status, q.Pending);      // returns stored pending
            Assert.Equal(UiSection.Unspecified, nav.TakePending()); // and CLEARS it
            Assert.False(showEvent.WaitOne(TimeSpan.Zero));         // query arm never signals
        }
    }

    [Fact]
    public void Navigate_EventVanished_PendingKept_AliveReportedFalse()
    {
        var (m, e) = UniqueNames();
        using var mutex = new Mutex(initiallyOwned: true, m, out _);
        // NO event created — shell "died" between probe and Set.
        var nav = new UiNavigationService(m, e);

        var r = nav.Navigate(UiSection.Status);

        Assert.False(r.UiAlive);                     // honest report
        Assert.Equal(UiSection.Status, r.Pending);   // pending kept for next show
        Assert.Equal(UiSection.Status, nav.TakePending());
    }

    // ── P3 2026-08-27: wake-consume contract (tray [Статус] full cycle) ─────
    // The running-shell path must be: store pending → signal the show event →
    // shell wake queries (UNSPECIFIED) → pending RETURNS and CLEARS. The query
    // arm additionally reports the HONEST live probe (was unconditional true).

    [Fact]
    public void Navigate_WakeConsume_FullCycle_StoreSignalQueryConsume()
    {
        var (m, e) = UniqueNames();
        using var holder = new HeldMutex(m);
        {
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, e);
            var nav = new UiNavigationService(m, e);

            // 1) Tray [Статус] while the shell RUNS: store + signal NOW.
            var stored = nav.Navigate(UiSection.Status);
            Assert.True(stored.UiAlive);
            Assert.True(showEvent.WaitOne(TimeSpan.Zero)); // wake signal fired

            // 2) Shell wake → query arm: returns the stored section, clears it,
            //    and carries the honest alive=true (the shell still holds).
            var consumed = nav.Navigate(UiSection.Unspecified);
            Assert.True(consumed.UiAlive);
            Assert.Equal(UiSection.Status, consumed.Pending);
            Assert.Equal(UiSection.Unspecified, nav.TakePending()); // read-and-clear
            Assert.False(showEvent.WaitOne(TimeSpan.Zero));         // query never signals
        }
    }

    [Fact]
    public void Navigate_QueryArm_NoShell_ReportsHonestUiAliveFalse()
    {
        var (m, e) = UniqueNames();
        var nav = new UiNavigationService(m, e);

        var q = nav.Navigate(UiSection.Unspecified);

        Assert.False(q.UiAlive);                     // was unconditional true — hid the dead-shell case
        Assert.Equal(UiSection.Unspecified, q.Pending); // nothing stored
    }
}
