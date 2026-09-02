namespace iCemDataMonitor.Contracts;

/// <summary>
/// Single source of truth for the gRPC named-pipe channel used by the host and
/// all local clients (D.6.2). No string literal for the pipe name may appear
/// outside this assembly.
/// </summary>
public static class HostChannelNames
{
    /// <summary>
    /// Named-pipe name for the local gRPC host channel.
    /// </summary>
    public const string PipeName = "iCemDataMonitor.Host";
}
