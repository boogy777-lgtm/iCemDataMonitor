// SignalWireCodec — decode a signal value (native units) from a 124-byte ACE
// payload. Shared by control reads and unit conversion (offset law:
// payload_index = ByteOffset − 4; FF=float32BE, UL/UI/HX=uintBE × DasFactor,
// SI/SL=intBE × DasFactor; UC/unknown ⇒ false). Used by the control channel
// and UnitConvertingPayloadProvider to read values before unit conversion.

using System.Buffers.Binary;
using System.Globalization;

namespace Halliburton.iCemEmule.Core;

public static class SignalWireCodec
{
    public static bool TryDecodeNative(byte[] payload, LantableRegistry.SignalDef sig, out double value)
    {
        value = 0;
        if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > payload.Length)
            return false;

        int off = sig.ByteOffset - 4;
        int sz = sig.Size;
        switch (sig.ValueType.ToUpperInvariant())
        {
            case "FF":
                if (sz != 4) return false;
                value = BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(off, 4));
                return true;

            case "UL":
            case "UI":
            case "HX":
                double rawU = sz switch
                {
                    1 => payload[off],
                    2 => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(off, 2)),
                    4 => BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(off, 4)),
                    8 => (double)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(off, 8)),
                    _ => double.NaN
                };
                if (double.IsNaN(rawU)) return false;
                value = rawU * sig.DasFactor;
                return true;

            case "SI":
            case "SL":
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
                return false;
        }
    }

    /// <summary>
    /// W11.2: FLECS 4.53 wire line formatter (moved from the UI layer — pure).
    /// One comma-separated token per EQUIPMENT variable; excludeFromTelemetry
    /// columns (Time &amp; Date, Unit Num, PLC Timestamp, ...) emit NOTHING.
    /// Formats: elapsed=F1, int=integer, else F2. InvariantCulture, no CRLF.
    /// </summary>
    public static string FormatLine(double[] values, DeviceTemplate template)
    {
        var parts = new List<string>(template.Variables.Length);
        for (int i = 0; i < template.Variables.Length; i++)
        {
            var v = template.Variables[i];
            if (v.ExcludeFromTelemetry)
                continue;   // not on the FLECS/PLC wire
            double val = i < values.Length ? values[i] : 0.0;

            if (string.Equals(v.Format, "elapsed", StringComparison.OrdinalIgnoreCase))
                parts.Add(val.ToString("F1", CultureInfo.InvariantCulture));
            else if (string.Equals(v.Format, "int", StringComparison.OrdinalIgnoreCase))
                parts.Add(((int)Math.Round(val)).ToString(CultureInfo.InvariantCulture));
            else
                parts.Add(val.ToString("F2", CultureInfo.InvariantCulture));
        }
        return string.Join(",", parts);
    }
}