using System.Threading;

namespace iCemDataMonitor.Host.Tray;

/// <summary>State of the terminal-command gate (pure three-state machine).</summary>
public enum TrayGateState
{
    /// <summary>No drain in progress — terminal commands are accepted.</summary>
    Idle = 0,

    /// <summary>[Перезапуск] accepted: successor spawned, drain in progress.</summary>
    RestartDraining = 1,

    /// <summary>[Закрыть] accepted: UI listeners signalled, drain in progress.</summary>
    CloseDraining = 2,
}

/// <summary>
/// P3 2026-08-27 (owner live-log: THREE host boots within 2 s — startup.log
/// 08:29:46-48 — from rapid «Перезапуск» clicks): re-entrancy gate for the two
/// terminal tray commands. [Перезапуск] spawns a successor and [Закрыть]
/// signals every UI listener; both then drain THIS process through
/// StopApplication. A second terminal click while a drain runs is an operator
/// error — it must be IGNORED and logged, never re-executed (a double-spawned
/// successor means two boots fighting for one mutex).
///
/// Three-state machine {Idle → RestartDraining | CloseDraining}: a transition
/// succeeds ONLY from Idle, so the two commands are mutually exclusive too —
/// «Перезапуск» during a close drain (and vice versa) is equally ignored.
/// Interlocked CAS keeps the gate correct even if a command ever arrives off
/// the tray pump thread.
/// </summary>
public sealed class TrayActionGate
{
    /// <summary>Evidence line for an ignored second [Перезапуск] click.</summary>
    public const string RestartBusyMessage =
        "перезапуск уже выполняется — повторный клик проигнорирован";

    /// <summary>Evidence line for an ignored second [Закрыть] click.</summary>
    public const string CloseBusyMessage =
        "закрытие уже выполняется — повторный клик проигнорирован";

    private const int IdleValue = (int)TrayGateState.Idle;
    private const int RestartValue = (int)TrayGateState.RestartDraining;
    private const int CloseValue = (int)TrayGateState.CloseDraining;

    private int _state = IdleValue;

    /// <summary>Current gate state (volatile observation — thread-safe).</summary>
    public TrayGateState State => (TrayGateState)Volatile.Read(ref _state);

    /// <summary>Human-readable state for evidence lines.</summary>
    public string StateDescription => State switch
    {
        TrayGateState.Idle => "idle",
        TrayGateState.RestartDraining => "restart-draining",
        TrayGateState.CloseDraining => "close-draining",
        _ => "unknown",
    };

    /// <summary>True when the gate accepts the click and a restart drain is
    /// now in progress; false when a terminal drain already runs (ignored).</summary>
    public bool TryBeginRestart() => TryBegin(RestartValue);

    /// <summary>True when the gate accepts the click and a close drain is
    /// now in progress; false when a terminal drain already runs (ignored).</summary>
    public bool TryBeginClose() => TryBegin(CloseValue);

    /// <summary>
    /// Release the gate back to Idle. Production call site: the successor
    /// SPAWN FAILED before any drain started — a stuck gate would otherwise
    /// lie «уже выполняется» and strand the operator with no way to retry.
    /// </summary>
    public void Reset() => Interlocked.Exchange(ref _state, IdleValue);

    private bool TryBegin(int target) =>
        Interlocked.CompareExchange(ref _state, target, IdleValue) == IdleValue;
}
