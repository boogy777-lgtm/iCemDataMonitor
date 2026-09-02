// ManualOverridePayloadProvider — additive decorator (EXEC: manual-edit → wire path).
// DEFECT FIX: LanTab.CommitEdit wrote ONLY the ListView cell text; operator-typed
// values never reached the multicast wire because frames are built each tick by
// TrayApp.OnTick via _lanProvider.GetSuperframe(). This decorator closes the
// loop: the UI commits edits here (IManualSignalOverride sink), and every
// GetSuperframe() patches the CLONED payload AFTER the inner provider (base
// AcePayloadProvider or DfbAcePayloadProvider) — the edited value is embedded at
// the signal's wire position payload[ByteOffset − 4] (кадр 0x18 + ByteOffset) with
// its wire encoding (ace_wire.signal_encoding).
//
// Overflow guard law (real cRIO, iCemCrio.pcapng.bin):
//   (ByteOffset − 4) + Size > PayloadLength(124) ⇒ signal NOT transmittable —
//   CanTransmit returns false with a reason; the UI marks the row / rejects the
//   edit. "Cmnt Ops Track Descript" (UC sz=120 @off=8 ⇒ payload_index 4, 4+120=124)
//   ТЕПЕРЬ передаваем (реальный cRIO его шлёт).
// Encoding is a local re-declaration of the frozen AcePayloadProvider.EncodeSignal
// law (ace_wire.signal_encoding, CONTRACT_iCemEmule §ace_wire) — pinned by
// FrameRoundTrip.Test5_ManualEditReachesWire byte evidence.

using System.Buffers.Binary;
using System.Text;

namespace Halliburton.iCemEmule.Core;

/// <summary>Manual-edit sink consumed by LanTab.CommitEdit (additive contract).</summary>
public interface IManualSignalOverride
{
    /// <summary>True ⇔ сигнал существует в superframe ∧ (ByteOffset−4)+Size ≤ 124.</summary>
    bool CanTransmit(uint svNumber, string signalName, out string reason);

    /// <summary>Set/clear the engineering-unit override (wire-encoded on the next frame tick).</summary>
    void SetOverride(uint svNumber, string signalName, double value);

    void ClearOverride(uint svNumber, string signalName);
}

public sealed class ManualOverridePayloadProvider : IAcePayloadProvider, IManualSignalOverride
{
    /// <summary>Base payload build size (ace_wire.superframe, AceFrameBuilder.PayloadSize).</summary>
    public const int PayloadLength = 124;

    private readonly IAcePayloadProvider _inner;
    private readonly LantableRegistry _lantable;
    private readonly Dictionary<(uint Sv, string Name), double> _overrides = new();
    private readonly object _overrideLock = new(); // guards _overrides (control channel + UI)

    public ManualOverridePayloadProvider(IAcePayloadProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _lantable = new LantableRegistry();
    }

    public IReadOnlyList<uint> ActiveSvNumbers => _inner.ActiveSvNumbers;

    public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
    {
        var vectors = _inner.GetSuperframe(equipmentNumber, elapsedSeconds);
        var result = new List<AceVectorData>(vectors.Count);
        foreach (var v in vectors)
        {
            var payload = (byte[])v.Payload.Clone();
            Patch(payload, v.SvNumber);
            result.Add(new AceVectorData(v.SvNumber, v.Name, payload));
        }
        return result;
    }

    private void Patch(byte[] payload, uint svNumber)
    {
        var svDef = _lantable.GetBySvNumber(svNumber);
        if (svDef is null)
        {
            return;
        }
        Dictionary<(uint Sv, string Name), double>? overrides;
        lock (_overrideLock)
        {
            overrides = _overrides.Count > 0
                ? new Dictionary<(uint Sv, string Name), double>(_overrides)
                : null;
        }
        if (overrides is null)
        {
            return;
        }
        foreach (var sig in svDef.Signals)
        {
            if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > payload.Length)
            {
                continue; // overflow guard (124 B payload law, payload_index = ByteOffset − 4)
            }
            if (!overrides.TryGetValue((svNumber, sig.Name), out double value))
            {
                continue;
            }
            byte[] encoded = EncodeSignal(value, sig.ValueType, sig.DasFactor, sig.Size);
            Array.Copy(encoded, 0, payload, sig.ByteOffset - 4, Math.Min(encoded.Length, sig.Size));
        }
    }

    public bool CanTransmit(uint svNumber, string signalName, out string reason)
    {
        var svDef = _lantable.GetBySvNumber(svNumber);
        if (svDef is null)
        {
            reason = $"SV 0x{svNumber:X8} not in lantable";
            return false;
        }
        var sig = Array.Find(svDef.Signals,
            s => string.Equals(s.Name, signalName, StringComparison.OrdinalIgnoreCase));
        if (sig is null)
        {
            reason = $"signal '{signalName}' not in SV 0x{svNumber:X8}";
            return false;
        }
        if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > PayloadLength)
        {
            // payload_index = ByteOffset − 4; "(4)+120=124" — передаваем (реальный cRIO шлёт).
            int payloadIndex = sig.ByteOffset - 4;
            reason = $"not transmittable: (ByteOffset {sig.ByteOffset} − 4) + Size {sig.Size} = " +
                     $"{payloadIndex + sig.Size} > {PayloadLength}";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public void SetOverride(uint svNumber, string signalName, double value)
    {
        lock (_overrideLock) { _overrides[(svNumber, signalName)] = value; }
    }

    public void ClearOverride(uint svNumber, string signalName)
    {
        lock (_overrideLock) { _overrides.Remove((svNumber, signalName)); }
    }

    /// <summary>Snapshot of active overrides (control channel status/vars).</summary>
    public IReadOnlyDictionary<(uint Sv, string Name), double> SnapshotOverrides()
    {
        lock (_overrideLock) { return new Dictionary<(uint Sv, string Name), double>(_overrides); }
    }

    // ── signal encoding (BE, ace_wire.signal_encoding) — same law as the frozen
    //    AcePayloadProvider.EncodeSignal; pinned by FrameRoundTrip T5 ──
    private static byte[] EncodeSignal(double value, string valueType, double dasFactor, int size)
    {
        return valueType.ToUpperInvariant() switch
        {
            "FF" => EncodeFloat32BE((float)value),
            "UL" => EncodeUInt32BE((uint)Math.Round(value * dasFactor)),
            "UI" => EncodeUInt32BE((uint)Math.Round(value * dasFactor)),
            "SI" => EncodeInt32BE((int)Math.Round(value * dasFactor)),
            "SL" => EncodeInt32BE((int)Math.Round(value * dasFactor)),
            "HX" => EncodeUInt32BE((uint)Math.Round(value)),
            "UC" => EncodeAsciiNulPad(value, size),
            _ => new byte[] { 0, 0, 0, 0 }
        };
    }

    private static byte[] EncodeFloat32BE(float v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return bytes;
    }

    private static byte[] EncodeUInt32BE(uint v)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
        return bytes;
    }

    private static byte[] EncodeInt32BE(int v)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, v);
        return bytes;
    }

    private static byte[] EncodeAsciiNulPad(double value, int size)
    {
        var text = value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        var buf = new byte[size];
        var count = Math.Min(text.Length, size);
        Encoding.ASCII.GetBytes(text, 0, count, buf, 0);
        return buf;
    }
}
