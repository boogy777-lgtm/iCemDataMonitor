// DfbAcePayloadProvider — IAcePayloadProvider-композитор (ADR_001, EXEC_DFBENGINE).
// Берёт базовый superframe (variable_registry путь) и перезаписывает байты сигналов,
// для которых ISignalValueProvider даёт значение. Fallback: TryGetValue=false ⇒ base-байты.
// AcePayloadProvider.cs НЕ тронут — аддитивный файл (frozen contract: additive ok).
// Кодирование: ace_wire.signal_encoding (CONTRACT_iCemEmule.yaml §1).
// Offset law (реальный cRIO): сигнал пишется на payload[ByteOffset − 4].

using System.Buffers.Binary;
using System.Text;
using Halliburton.iCemEmule.Dfb;

namespace Halliburton.iCemEmule.Core;

public sealed class DfbAcePayloadProvider : IAcePayloadProvider
{
    private readonly AcePayloadProvider _base;
    private readonly ISignalValueProvider _values;
    private readonly LantableRegistry _lantable;

    public IReadOnlyList<uint> ActiveSvNumbers => _base.ActiveSvNumbers;

    public DfbAcePayloadProvider(AcePayloadProvider baseProvider, ISignalValueProvider values)
    {
        _base = baseProvider ?? throw new ArgumentNullException(nameof(baseProvider));
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _lantable = new LantableRegistry();
    }

    public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
    {
        var vectors = _base.GetSuperframe(equipmentNumber, elapsedSeconds);
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
        if (svDef is null) return;

        foreach (var sig in svDef.Signals)
        {
            // Закон раскладки (реальный cRIO): сигнал пишется на payload[O − 4].
            if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > payload.Length)
                continue; // overflow guard
            if (!_values.TryGetValue(sig.Name, out double value))
                continue; // fallback: base-байты (variable_registry / 0)

            var encoded = Encode(value, sig.ValueType, sig.DasFactor, sig.Size);
            Array.Copy(encoded, 0, payload, sig.ByteOffset - 4, Math.Min(encoded.Length, sig.Size));
        }
    }

    // ── сигнальное кодирование (BE, ace_wire.signal_encoding) ──
    private static byte[] Encode(double value, string valueType, double dasFactor, int size)
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
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
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

/// <summary>Адаптер DFBEngine.DfbSignalSource → Core.ISignalValueProvider.</summary>
public sealed class DfbSignalSourceAdapter : ISignalValueProvider
{
    private readonly DfbSignalSource _source;

    public DfbSignalSourceAdapter(DfbSignalSource source) => _source = source;

    public bool TryGetValue(string wireName, out double value) => _source.TryGetValue(wireName, out value);

    public double GetValue(string wireName) => _source.GetValue(wireName);
}

/// <summary>Точка сборки: создаёт DFB-сеть и декорирует базовый провайдер.
/// Graceful fallback: любая ошибка DFB ⇒ работаем на базовом провайдере.</summary>
public static class DfbEngineBridge
{
    public const int Seed = 7;

    public static DfbNetwork? TryCreateNetwork()
    {
        try { return CementUnitNetworkFactory.Create(); }
        catch { return null; }
    }

    public static IAcePayloadProvider Decorate(AcePayloadProvider baseProvider, DfbNetwork network)
    {
        var source = new DfbSignalSource(network);
        return new DfbAcePayloadProvider(baseProvider, new DfbSignalSourceAdapter(source));
    }
}
