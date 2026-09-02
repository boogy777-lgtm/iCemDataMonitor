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
    public async Task Navigate_AliveUi_SignalsShowEvent()
    {
        var (m, e) = UniqueNames();
        // A mutex is REENTRANT per thread — if THIS test thread owned it,
        // MutexProbe would see "free". Hold it from another thread instead.
        using var held = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var m2 = new Mutex(true, m, out _);
            held.Set();
            Thread.Sleep(10_000); // lives for the duration of the test only
        });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "holder thread failed to take the mutex");
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, e);
            var nav = new UiNavigationService(m, e);

            // 2026-08-27: only UiSection.Status remains (web/emulator reserved).
            var r = nav.Navigate(UiSection.Status);

            Assert.True(r.UiAlive);
            Assert.Equal(UiSection.Status, r.Pending);
            Assert.True(showEvent.WaitOne(TimeSpan.Zero)); // the shell wake-up signal fired
            Assert.Equal(UiSection.Status, nav.TakePending()); // still stored for late consumers
        }
        finally
        {
            await holder.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task Navigate_Unspecified_IsQueryArm_DoesNotSignal()
    {
        var (m, e) = UniqueNames();
        using var held = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var m2 = new Mutex(true, m, out _);
            held.Set();
            Thread.Sleep(10_000);
        });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "holder thread failed to take the mutex");
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
        finally
        {
            await holder.WaitAsync(TimeSpan.FromSeconds(15));
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
    public async Task Navigate_WakeConsume_FullCycle_StoreSignalQueryConsume()
    {
        var (m, e) = UniqueNames();
        using var held = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var m2 = new Mutex(true, m, out _);
            held.Set();
            Thread.Sleep(10_000); // lives for the duration of the test only
        });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "holder thread failed to take the mutex");
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
        finally
        {
            await holder.WaitAsync(TimeSpan.FromSeconds(15));
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
