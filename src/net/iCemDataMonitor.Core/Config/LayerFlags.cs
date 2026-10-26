using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Core.Config;

/// <summary>
/// Layer activation plan derived from ini flags (TZ_ARCH_V2 §5 layers + §6 flags).
/// OFF ⇒ the layer is NOT registered and NOT started at all — the composition
/// root branches on this plan. Pure function — testable without a host.
/// OPC DA is a stub by recon: the flag is honored (off = nothing, on =
/// log-only, the external OPC server owns projections — W3).
///
/// 2026-08-27: Web RESTORED (the owner decree «Web - удали (контрол sim)»
/// meant removing the sim control from the dashboard page, NOT the page —
/// corrected decree; the web/SPA layer is back: wwwroot + gRPC-Web + ring
/// history). Web is startup-only (composition branch) and its tray toggle is
/// persist-only (Kestrel in-process cannot re-bind without a restart).
/// The emulator was never a layer here (it was engine supervision in Host.Core,
/// deleted — the emulator is a separate independent app now).
/// </summary>
public readonly record struct LayerFlags(bool Modbus, bool LanSniffer, bool OpcDa, bool Web)
{
    public static LayerFlags From(IniOptions ini) => new(
        ini.ModbusOn == 1,
        ini.LansnifferOn == 1,
        ini.OpcdaOn == 1,
        ini.WebOn == 1);
}
