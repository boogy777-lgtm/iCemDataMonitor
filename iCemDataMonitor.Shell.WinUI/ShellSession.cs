using System.Reactive.Linq;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.HostClient;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Shell.WinUI;

/// <summary>
/// D.5/B.2.1: WHAT the shell talks to after the ensure-host flow.
///
/// Client mode  — gRPC over the host named pipe: SubscribeMetrics stream +
///                periodic GetStatus + control RPCs. The shell hosts NOTHING.
/// ViewerOnly   — «Только просмотр bus»: the host is NOT running, so the shell
///                reads the bus DIRECTLY, read-only, WITHOUT any control and
///                with a permanent honest banner. This is the sanctioned
///                exception to «BusReader lives only in the host»: in this
///                mode no other process reads the bus (the host is down), so
///                the B.5 double-reader drift cannot occur; the reader is
///                created lazily and disposed with the session.
/// Public only because MainWindow exposes it on its public surface (WinExe —
/// no external consumers).
/// </summary>
public sealed class ShellSession : IDisposable
{
    public enum ModeKind { Client, ViewerOnly }

    private readonly Timer? _watchdog;

    public ModeKind Mode { get; }
    public HostLink? Link { get; }              // client mode only
    public GrpcHostClient? ControlApi { get; }  // client mode only (unary control RPCs)

    private BusReader? _busReader; // viewer mode only, lazy

    private ShellSession(ModeKind mode, HostLink? link, GrpcHostClient? control)
    {
        Mode = mode;
        Link = link;
        ControlApi = control;
        if (link is not null)
            _watchdog = new Timer(_ => link.CheckStale(), null, 1000, 1000);
    }

    public static ShellSession CreateClient()
    {
        var link = new HostLink();
        link.Start();
        return new ShellSession(ModeKind.Client, link, new GrpcHostClient());
    }

    public static ShellSession CreateViewerOnly() => new(ModeKind.ViewerOnly, null, null);

    /// <summary>
    /// ~1 Hz status ticks for the status page. Client: GetStatus RPC — errors
    /// collapse to an EMPTY HostStatus («нет данных» facts); the connection
    /// banner is owned by the HostLink FSM, not by these ticks. Viewer: direct
    /// read-only bus snapshot mapped onto the same proto shape.
    /// P3 2026-08-27: StartWith = an IMMEDIATE first fetch — without it the
    /// status page (and the console checkbox) showed the initial defaults for
    /// the first ~1 s and only then the host's truth (owner complaint
    /// «ConsoleSwitch не отражает состояние хоста при загрузке»).
    /// </summary>
    public IObservable<HostStatus> StatusTicks() =>
        Observable.Interval(TimeSpan.FromSeconds(1))
            .Select(_ => Observable.FromAsync(FetchStatusAsync))
            .StartWith(Observable.FromAsync(FetchStatusAsync))
            .Concat();

    private async Task<HostStatus> FetchStatusAsync()
    {
        if (Mode == ModeKind.Client)
        {
            try
            {
                return await Link!.GetStatusAsync();
            }
            catch
            {
                return new HostStatus(); // honest empty facts while the FSM reconnects
            }
        }

        _busReader ??= OpenBusReader();
        return MapSnapshot(_busReader);
    }

    private static BusReader OpenBusReader()
    {
        var reader = new BusReader(); // ProgramData defaults (same file the host writes)
        reader.TryOpen(out _); // failure ⇒ empty facts, never a crash of the viewer
        return reader;
    }

    internal static HostStatus MapSnapshot(BusReader reader)
    {
        var snap = reader.LastSnapshot;
        var status = new HostStatus { BusOpen = reader.IsOpen && snap is not null };
        if (snap is null)
            return status;

        status.Heartbeat = snap.Heartbeat;
        status.ComPort = snap.ComPort;
        status.CaseState = snap.CaseState;
        // 2026-08-28 dishonest-surface fix: case_lan_state = header reg 2
        // (tap-exposed LAN/cRIO state) — was snap.LanStatus = header reg 8,
        // which is a com_stream float-pair low word in v5 (garbage).
        status.CaseLanState = snap.CaseLanState;
        status.Zones.Add(Zone(BusZoneKind.BusZoneCom, snap.ZoneInfo(ZoneKind.ComMain)));
        status.Zones.Add(Zone(BusZoneKind.BusZoneLan, snap.ZoneInfo(ZoneKind.Lan)));
        status.Zones.Add(Zone(BusZoneKind.BusZoneRaw, snap.ZoneInfo(ZoneKind.RawReserve)));
        return status;
    }

    private static ZoneHealth Zone(BusZoneKind kind, BusZoneInfo info) => new()
    {
        Kind = kind,
        Active = info.Active,
        AgeMs = info.AgeMs(),
    };

    public void Dispose()
    {
        _watchdog?.Dispose();
        Link?.Dispose();
        ControlApi?.Dispose();
        _busReader?.Dispose();
    }
}
