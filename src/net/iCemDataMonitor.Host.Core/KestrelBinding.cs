using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// D.3.4 binding decision for the headless host gRPC/ops surface.
/// 2026-08-27 (corrected decree): web_remote_on is BACK — the web/SPA browser
/// surface is restored. The decision table has TWO arms:
///   webRemoteOn=false → IPAddress.Loopback (default; local CLI/host-to-host
///   tools and the local browser),
///   webRemoteOn=true  → IPAddress.Any (remote browser inside the CLOSED
///   security contour; non-discrimination — not a single IP-guard exists).
/// Both arms keep Http1AndHttp2 (/api/* JSON + gRPC on one socket).
///
/// Client non-discrimination law: NO code path may branch on the caller's IP —
/// calls are anonymous (E.1, owner decree 2026-08-24: authorization = contour
/// isolation, ¬token). This type decides WHERE the socket listens, never WHO
/// may call.
/// Pure function + applier: unit tests pin the decision table without opening
/// real sockets.
/// </summary>
public sealed record KestrelBindPlan(IPAddress Address, int Port, HttpProtocols Protocols);

public static class KestrelBinding
{
    public const int DefaultPort = 8090;

    /// <summary>
    /// Decision table: (port, webRemoteOn) → bind plan. Loopback is the
    /// fail-safe arm (web_remote_on absent/0 ⇒ loopback-only).
    /// </summary>
    public static KestrelBindPlan Plan(int port, bool webRemoteOn)
    {
        if (port is < 1 or > 65535 && port != 0) // 0 = ephemeral (tests)
            throw new ArgumentOutOfRangeException(nameof(port), $"port out of range [1..65535] (or 0 for tests): {port}");
        return new KestrelBindPlan(
            Address: webRemoteOn ? IPAddress.Any : IPAddress.Loopback,
            Port: port,
            Protocols: HttpProtocols.Http1AndHttp2);
    }

    /// <summary>Apply the plan to Kestrel options (single listen endpoint).</summary>
    public static void Apply(KestrelServerOptions options, KestrelBindPlan plan) =>
        options.Listen(plan.Address, plan.Port, listenOptions => listenOptions.Protocols = plan.Protocols);
}
