using System.Buffers.Binary;

namespace iCemDataMonitor.Transport.Tests;

public sealed record PcapPacket(long TimestampNs, int InterfaceId, byte[] Frame);

public sealed class PcapCapture
{
    public List<PcapPacket> Packets { get; } = new();
    public List<int> LinkTypes { get; } = new();
}

/// <summary>
/// Minimal pcapng reader for ACE replay tests.
/// Supports SHB, IDB, EPB, SPB block types, little-endian only.
/// Ported from tools/ace_replay/PcapngReader.cs.
/// </summary>
public static class PcapngReader
{
    private const uint BlockSectionHeader = 0x0A0D0D0A;
    private const uint BlockInterfaceDescription = 0x00000001;
    private const uint BlockSimplePacket = 0x00000003;
    private const uint BlockEnhancedPacket = 0x00000006;
    private const uint ByteOrderMagicLe = 0x1A2B3C4D;

    public static PcapCapture Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var cap = new PcapCapture();
        var tsScaleNs = new List<double>();

        int pos = 0;
        while (pos + 12 <= data.Length)
        {
            uint blockType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            uint blockLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            if (blockLen < 12 || pos + blockLen > data.Length)
                throw new InvalidDataException($"Corrupt pcapng block at offset {pos}: type 0x{blockType:X8} len {blockLen}");

            ReadOnlySpan<byte> body = data.AsSpan(pos + 8, (int)blockLen - 12);

            switch (blockType)
            {
                case BlockSectionHeader:
                {
                    uint bom = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    if (bom != ByteOrderMagicLe)
                        throw new InvalidDataException($"Unsupported pcapng byte order 0x{bom:X8} (only little-endian captures supported)");
                    break;
                }
                case BlockInterfaceDescription:
                {
                    int linkType = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    cap.LinkTypes.Add(linkType);
                    tsScaleNs.Add(ParseTsResolutionNs(body));
                    break;
                }
                case BlockEnhancedPacket:
                {
                    int interfaceId = BinaryPrimitives.ReadInt32LittleEndian(body);
                    uint tsHigh = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4));
                    uint tsLow = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8));
                    int capLen = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(12));
                    long rawTs = ((long)tsHigh << 32) | tsLow;
                    double scale = interfaceId < tsScaleNs.Count ? tsScaleNs[interfaceId] : 1_000.0;
                    long tsNs = (long)(rawTs * scale);
                    byte[] frame = body.Slice(20, capLen).ToArray();
                    cap.Packets.Add(new PcapPacket(tsNs, interfaceId, frame));
                    break;
                }
                case BlockSimplePacket:
                {
                    int origLen = BinaryPrimitives.ReadInt32LittleEndian(body);
                    int avail = Math.Min(origLen, body.Length - 4);
                    byte[] frame = body.Slice(4, avail).ToArray();
                    cap.Packets.Add(new PcapPacket(0, 0, frame));
                    break;
                }
            }

            pos += (int)blockLen;
        }

        return cap;
    }

    private static double ParseTsResolutionNs(ReadOnlySpan<byte> idbBody)
    {
        int off = 8;
        while (off + 4 <= idbBody.Length)
        {
            int code = BinaryPrimitives.ReadUInt16LittleEndian(idbBody.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(idbBody.Slice(off + 2));
            if (code == 0) break;
            if (code == 9 && len >= 1)
            {
                byte v = idbBody[off + 4];
                double secondsPerTick = (v & 0x80) != 0
                    ? Math.Pow(2, -(v & 0x7F))
                    : Math.Pow(10, -v);
                return secondsPerTick * 1e9;
            }
            off += 4 + ((len + 3) & ~3);
        }
        return 1_000.0;
    }
}