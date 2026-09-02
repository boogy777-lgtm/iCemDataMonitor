using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// W21.3 host-side UI navigation coordinator. The HOST owns the
/// pending-navigation state — the shell is a dumb view that applies whatever
/// the host tells it. 2026-08-27: the tray [Статус] button was REMOVED with
/// the web/SPA layer — the hierarchical CLI section commands now land on this
/// path (NavigateUi RPC, only UiSection.Status remains in the contract).
///
/// Semantics of Navigate (closed-arm, mirrors NavigateUiResponse):
/// - section != UNSPECIFIED → store pending; if the shell mutex is TAKEN
///   (ui alive) also SET the show event so an already-running shell wakes up
///   and consumes the pending section now;
/// - section == UNSPECIFIED → QUERY ARM: return and CLEAR the pending section
///   without setting anything. This is how the shell learns WHERE to open
///   when it is shown (no extra contract surface needed). ui_alive carries the
///   HONEST live probe (P3 2026-08-27 — was an unconditional true).
///
/// If the show event disappears between the mutex probe and the Set (shell
/// died in that window) the pending section stays STORED and ui_alive is
/// reported honestly as false — the next shell show will consume it.
/// </summary>
public sealed class UiNavigationService
{
    private readonly object _lock = new();
    private UiSection _pending = UiSection.Unspecified;
    private readonly string _shellMutexName;
    private readonly string _showEventName;
    private readonly Action<string>? _log;

    /// <summary>Names are injectable for unit tests; production uses the
    /// CrossProcessNames constants.</summary>
    public UiNavigationService(
        string? shellMutexName = null,
        string? showEventName = null,
        Action<string>? log = null)
    {
        _shellMutexName = shellMutexName ?? CrossProcessNames.ShellMutex;
        _showEventName = showEventName ?? CrossProcessNames.ShellShowWindowEvent;
        _log = log;
    }

    public NavigateUiResponse Navigate(UiSection section)
    {
        if (section == UiSection.Unspecified)
        {
            // QUERY arm (shell wake / first show): read-and-clear the pending
            // section. P3 2026-08-27: ui_alive was reported TRUE unconditionally
            // — the honest PROBE goes out so the shell's log never misreports a
            // dead-shell wake as a live one (owner complaint «Статус мёртв» was
            // undiagnosable under the unconditional lie).
            return new NavigateUiResponse
            {
                UiAlive = MutexProbe.IsTaken(_shellMutexName),
                Pending = TakePending(),
            };
        }

        bool alive = MutexProbe.IsTaken(_shellMutexName);
        lock (_lock)
            _pending = section;

        if (!alive)
        {
            _log?.Invoke($"[nav] pending={section} stored (shell not running)");
            return new NavigateUiResponse { UiAlive = false, Pending = section };
        }

        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(_showEventName);
            showEvent.Set();
            _log?.Invoke($"[nav] signalled {_showEventName} → {section}");
            return new NavigateUiResponse { UiAlive = true, Pending = section };
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Shell died between the probe and the Set — pending stays stored.
            _log?.Invoke($"[nav] show-event vanished; pending={section} kept for next shell show");
            return new NavigateUiResponse { UiAlive = false, Pending = section };
        }
        catch (UnauthorizedAccessException ex)
        {
            // P3 2026-08-27 (owner complaint «Статус мёртв»): cross-integrity
            // shell/host (the elevated-iCem stand topology) makes the event
            // EXIST but NOT SETTABLE from this process — the old code let this
            // escape to HostTray.Guarded, where it surfaced only as a generic
            // FAILED line while the running shell never woke. Honest report +
            // pending kept: the shell consumes it on its next show.
            _log?.Invoke($"[nav] show-event not signallable (integrity/ACL?): {ex.Message}; " +
                         $"pending={section} kept for next shell show");
            return new NavigateUiResponse { UiAlive = false, Pending = section };
        }
    }

    /// <summary>Shell-side consumption: read-and-clear (query arm).</summary>
    public UiSection TakePending()
    {
        lock (_lock)
        {
            var p = _pending;
            _pending = UiSection.Unspecified;
            return p;
        }
    }
}
