namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>One already-resolved pair; no vendor objects, queue or bus I/O.</summary>
    internal struct PreparedPairWrite
    {
        public readonly ZoneKind Zone;
        public readonly int Address;
        public readonly ushort Low;
        public readonly ushort High;

        public PreparedPairWrite(ZoneKind zone, int address, ushort low, ushort high)
        {
            Zone = zone; Address = address; Low = low; High = high;
        }
    }

    internal static class PollPairPreparation
    {
        public static PreparedPairWrite PrepareCom(PollResolution.Entry entry)
        {
            // Time & Date alone is int32 Unix seconds; Unit Num stays float32.
            if (entry.Address == BusLayout.ComTimestampReg)
            {
                uint bits = unchecked((uint)UnixTimestamp.TicksToUnixSeconds(entry.Value));
                return new PreparedPairWrite(ZoneKind.ComMain, entry.Address,
                    (ushort)(bits & 0xFFFF), (ushort)(bits >> 16));
            }
            return FloatPair(ZoneKind.ComMain, entry);
        }

        public static PreparedPairWrite PrepareLan(PollResolution.Entry entry)
        {
            return FloatPair(ZoneKind.Lan, entry);
        }

        private static PreparedPairWrite FloatPair(ZoneKind zone, PollResolution.Entry entry)
        {
            ushort low, high;
            CdabCodec.Split((float)entry.Value, out low, out high);
            return new PreparedPairWrite(zone, entry.Address, low, high);
        }
    }
}
