namespace iCemDataMonitor.HostClient;

/// <summary>
/// Pure formatter for the shell tray tooltip (B.2.2/A.3.2 heritage): dynamic
/// host alive/dead indicator + heartbeat pulse. Unit-tested.
/// </summary>
public static class HostTooltip
{
    public static string Format(bool hostAlive, int heartbeat) =>
        hostAlive
            ? $"iCem Data Monitor — хост работает · пульс {heartbeat}"
            : "iCem Data Monitor — хост НЕ запущен";
}
