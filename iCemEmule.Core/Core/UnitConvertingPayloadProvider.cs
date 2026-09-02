// UnitConvertingPayloadProvider — САМЫЙ ВНЕШНИЙ декоратор цепочки провайдеров.
// Преобразует payload каждого вектора superframe из НАТИВНЫХ единиц сигналов
// (lantable_registry.json, напр. bbl/min, °F, lb/gal) в целевые единицы: пресета
// (UnitPreset) либо детальных настроек UnitSettings (новая вкладка UI «Единицы»,
// свойство Settings). Значения на проводе всегда в целевых единицах — UI показывает
// их как есть, а ManualOverride/CommitEdit хранят wire-единицы (native).
//
// Цепочка (внешний → внутренний):
//   UnitConvertingPayloadProvider( ManualOverridePayloadProvider( DfbAcePayloadProvider( AcePayloadProvider ) ) )
//
// Декодирование/кодирование — ace_wire.signal_encoding (BE), тот же закон, что в
// AcePayloadProvider/ManualOverridePayloadProvider. Непроверяемые сигналы (FF не 4
// байта, UC-строки, overflow (ByteOffset−4)+Size>124) пропускаются без изменений.
// Offset law (реальный cRIO): сигнал лежит на payload[ByteOffset − 4].
// R1: никаких ссылок на MongoDB.

using System.Buffers.Binary;

namespace Halliburton.iCemEmule.Core;

public sealed class UnitConvertingPayloadProvider : IAcePayloadProvider
{
    private readonly IAcePayloadProvider _inner;
    private readonly LantableRegistry _lantable;

    /// <summary>Текущий пресет единиц (переключается из LanTab в каждый тик).</summary>
    public UnitPreset Preset { get; set; }

    /// <summary>Детальные настройки единиц (новая вкладка UI «Единицы»).
    /// Null = следовать пресету (Preset). Непустой Settings переопределяет целевую
    /// единицу по каждой группе сигнала через UnitSettings.TargetUnit.</summary>
    public UnitSettings? Settings { get; set; }

    /// <summary>Внутренний провайдер (для LanTab: разрешение IManualSignalOverride).</summary>
    public IAcePayloadProvider Inner => _inner;

    public UnitConvertingPayloadProvider(IAcePayloadProvider inner, UnitPreset preset, LantableRegistry? lantable = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Preset = preset;
        _lantable = lantable ?? new LantableRegistry();
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
            return;

        // Детальные настройки вкладки (Settings) либо временные по пресету — для NeedsConversion.
        var settings = Settings ?? new UnitSettings { Preset = Preset };

        foreach (var sig in svDef.Signals)
        {
            // Overflow guard (124 B payload law, payload_index = ByteOffset − 4)
            // + UC-строки/неизвестные типы пропускаем.
            if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > payload.Length)
                continue;

            // БАГ B: под FirmwareDefault firmware-native единицы (gal/min, °c, lb/gal, g/cc,
            // bbl, ft, psi) — identity (прошивка передаёт их 1:1; кейс MB_DensHPVT g/cc, который
            // раньше ошибочно ×8.3454 пересчитывался в ppg). Также покрывает None и native==target.
            if (!UnitConversion.NeedsConversion(sig.UoM, settings))
                continue;

            var group = UnitConversion.GroupOf(sig.UoM);

            // Целевая единица: детальные настройки вкладки (Settings) либо пресет.
            string target = settings.TargetUnit(group);

            if (!TryDecode(payload, sig, out double wireValue))
                continue; // декод невозможен → сигнал без изменений

            // Общий конвертер: даёт те же множители (42 / (v−32)×5÷9 / 0.119826 / 0.3048 / 0.0689476), что и старый путь.
            double presetValue = UnitConversion.Convert(wireValue, sig.UoM, target);
            if (TryEncode(presetValue, sig, out byte[] encoded))
                Array.Copy(encoded, 0, payload, sig.ByteOffset - 4, Math.Min(encoded.Length, sig.Size));
        }
    }

    // ── декодирование байт в wire-значение (native units) ────────────────
    private static bool TryDecode(byte[] payload, LantableRegistry.SignalDef sig, out double value)
    {
        value = 0;
        if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > payload.Length)
            return false;

        int off = sig.ByteOffset - 4;   // payload_index = ByteOffset − 4
        int sz = sig.Size;
        switch (sig.ValueType.ToUpperInvariant())
        {
            case "FF": // IEEE 754 float32, big-endian (ace_wire.signal_encoding)
                if (sz != 4) return false;
                value = BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(off, 4));
                return true;

            case "UL":
            case "UI":
            case "HX": // unsigned integer, big-endian
                double rawU = sz switch
                {
                    1 => payload[off],
                    2 => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(off, 2)),
                    4 => BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(off, 4)),
                    8 => (double)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(off, 8)),
                    _ => double.NaN
                };
                if (double.IsNaN(rawU)) return false;
                value = rawU * sig.DasFactor; // wire value = raw × dasFactor
                return true;

            case "SI":
            case "SL": // signed integer, big-endian
                double rawS = sz switch
                {
                    1 => (sbyte)payload[off],
                    2 => BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(off, 2)),
                    4 => BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(off, 4)),
                    8 => (double)BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(off, 8)),
                    _ => double.NaN
                };
                if (double.IsNaN(rawS)) return false;
                value = rawS * sig.DasFactor;
                return true;

            default:
                return false; // UC и неизвестные типы — пропуск
        }
    }

    // ── кодирование preset-значения обратно в байты (нативный wire) ──────
    private static bool TryEncode(double value, LantableRegistry.SignalDef sig, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        int sz = sig.Size;
        switch (sig.ValueType.ToUpperInvariant())
        {
            case "FF":
                if (sz != 4) return false;
                encoded = EncodeFloat32BE((float)value);
                return true;

            case "UL":
            case "UI":
            case "HX":
                return EncodeUnsigned(value / sig.DasFactor, sz, out encoded);

            case "SI":
            case "SL":
                return EncodeSigned(value / sig.DasFactor, sz, out encoded);

            default:
                return false;
        }
    }

    // Защита от переполнения и отрицательных значений для unsigned.
    private static bool EncodeUnsigned(double value, int size, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        switch (size)
        {
            case 1:
                encoded = new[] { (byte)Math.Clamp(Math.Round(value), 0.0, byte.MaxValue) };
                return true;
            case 2:
                encoded = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)Math.Clamp(Math.Round(value), 0.0, ushort.MaxValue));
                return true;
            case 4:
                encoded = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)Math.Clamp(Math.Round(value), 0.0, uint.MaxValue));
                return true;
            case 8:
                encoded = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(encoded, (ulong)Math.Clamp(Math.Round(value), 0.0, ulong.MaxValue));
                return true;
            default:
                return false;
        }
    }

    private static bool EncodeSigned(double value, int size, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        switch (size)
        {
            case 1:
                encoded = new[] { unchecked((byte)(sbyte)Math.Clamp(Math.Round(value), sbyte.MinValue, sbyte.MaxValue)) };
                return true;
            case 2:
                encoded = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(encoded, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
                return true;
            case 4:
                encoded = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(encoded, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
                return true;
            case 8:
                encoded = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(encoded, (long)Math.Clamp(Math.Round(value), long.MinValue, long.MaxValue));
                return true;
            default:
                return false;
        }
    }

    private static byte[] EncodeFloat32BE(float v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }
}
