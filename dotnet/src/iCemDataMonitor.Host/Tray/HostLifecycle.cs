using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// 2026-08-31 (owner decree «единый механизм запуска/рестарта/закрытия/
/// сворачивания — использовать везде: трей, кнопки UI, кнопки закрытия»):
/// the ONE lifecycle coordinator of the host process — a miniature SCM
/// (Service Control Manager). EVERY terminal verb routes here: the tray
/// menu [Перезапуск]/[Закрыть] AND the shell's named-event pokes
/// (HostRestartEvent) call the SAME methods — the mechanism is a code
/// fact, not a convention. E.2.7 intact: clients never kill the host;
/// they signal, the host terminates ITSELF.
///
/// Microsoft-pattern mapping (why this shape): the SCM owns service
/// process state and receives CONTROL CODES over a dispatcher — services
/// are never killed by clients; IIS/WAS worker-process RECYCLING =
/// spawn successor → overlap → old exits, which is exactly
/// <see cref="RequestRestart"/> (RestartPlan spawn + grace-waiting
/// successor + blunt exit); RegisterApplicationRestart = the successor
/// inherits the original command line (RestartPlan.Build copies args).
/// Console minimize/close is NOT here — it already has its own single
/// mechanism (ConsoleVisibilityService, owner law 2026-08-28).
/// Test seams (§15.7 (a)): spawn/exit/signal are injectable; production
/// defaults are the real OS verbs.
/// </summary>
public sealed class HostLifecycle
{
    private readonly string[] _args;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Action<string> _log;
    private readonly TrayActionGate _gate = new();

    private readonly Func<ProcessStartInfo, Process?> _spawn;
    private readonly Action<int> _exit;
    private readonly Func<string, bool> _signalEvent;

    public HostLifecycle(
        string[] args,
        IHostApplicationLifetime lifetime,
        Action<string> log,
        Func<ProcessStartInfo, Process?>? spawn = null,
        Action<int>? exit = null,
        Func<string, bool>? signalEvent = null)
    {
        _args = args;
        _lifetime = lifetime;
        _log = log;
        _spawn = spawn ?? Process.Start;
        _exit = exit ?? Environment.Exit;
        _signalEvent = signalEvent ?? SignalNamedEvent;
    }

    /// <summary>Assigned once the tray exists — the restart's only pre-exit
    /// chore is the icon hide (Windows 11 ghost-icon lesson).</summary>
    public HostTray? Tray { get; set; }

    /// <summary>
    /// [Перезапуск] — tray menu AND shell button (HostRestartEvent).
    /// 2026-08-31 (owner decree «тупо закрывать процесс — сохранение данных
    /// не нужно, через 1 секунду будут новые от dll»): the restart exit is a
    /// BLUNT self-exit, NOT a graceful drain. The host is stateless across
    /// restarts (the AddIn re-feeds the bus at ~1 Hz, every client
    /// reconnects by design), so there is nothing to drain FOR; the OS
    /// releases every handle (named pipe, :502, MMF view, single-instance
    /// mutex) at process exit, and the successor's 500 ms grace poll picks
    /// the mutex up within ~0.5 s. [Закрыть] keeps the graceful path.
    /// </summary>
    public void RequestRestart()
    {
        if (!_gate.TryBeginRestart())
        {
            _log(TrayActionGate.RestartBusyMessage);
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Environment.ProcessPath is null — cannot self-restart");
            var plan = RestartPlan.Build(exePath, _args);
            using var successor = _spawn(plan);
            _log($"self-restart: successor spawned pid={successor?.Id}; blunt exit (no drain — stateless host)");
        }
        catch (Exception ex)
        {
            // The successor did NOT spawn — release the gate so the operator
            // can retry. A stuck gate would lie «уже выполняется» while the
            // host keeps running normally.
            _gate.Reset();
            _log($"self-restart: successor spawn FAILED ({ex.Message}) — gate released, host keeps running; retry possible");
            return;
        }

        try { Tray?.Dispose(); } catch { /* icon hide is best-effort pre-exit */ }
        _log("host stopped (fast restart exit)");
        _exit(0); // mutex released by process exit — successor takes over
    }

    /// <summary>
    /// [Закрыть] — P2 2026-08-26 (owner decree): close the UI + the host.
    /// Signal the panel's shutdown event (the emulator is INDEPENDENT —
    /// never signalled), then drain THIS process through the existing
    /// graceful stop path (no successor, no time pressure; the shell must
    /// receive its event BEFORE the pipe dies).
    /// </summary>
    public void RequestClose()
    {
        if (!_gate.TryBeginClose())
        {
            _log(TrayActionGate.CloseBusyMessage);
            return;
        }

        foreach (var name in TrayCloseContract.UiShutdownEventNames)
        {
            _log(_signalEvent(name)
                ? $"[close] {name} signalled"
                : $"[close] {name} not running — nothing to signal");
        }
        _lifetime.StopApplication();
    }

    /// <summary>Production event poke: true = signalled, false = nobody
    /// listens (honest either way — the close path logs the outcome).
    /// 2026-08-31 (слой CLI): internal — the CLI forwarder uses the SAME
    /// poke for its lifecycle verbs (one mechanism, no duplicated code).</summary>
    internal static bool SignalNamedEvent(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
