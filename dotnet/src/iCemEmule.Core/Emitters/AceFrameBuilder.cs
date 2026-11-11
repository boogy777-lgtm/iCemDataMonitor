using System;

namespace Halliburton.iCemEmule.Emitters
{
    /// <summary>
    /// Static builder/parser for 152-byte ACE raw 802.3 frames.
    /// Implements ace_wire constants from CONTRACT_iCemEmule.yaml and PROTOCOL.md §1.
    /// </summary>
    public static class AceFrameBuilder
    {
        public const int FrameSize = 152;
        public const int PayloadSize = 124;
        public const int PayloadOffset = 0x1C;       // decimal 28
        public const int EquipmentOffset = 0x14;     // decimal 20
        public const int SvOffset = 0x18;            // decimal 24
        public const int LengthOffset = 0x0C;        // decimal 12
        public const int LlcOffset = 0x0E;           // decimal 14
        public const int MagicOffset0 = 0x11;        // decimal 17
        public const int MagicOffset1 = 0x12;        // decimal 18
        public const int MagicOffset2 = 0x13;        // decimal 19

        private static readonly byte[] DstMacBytes = ParseMacInternal("03:00:01:00:00:20");
        private const ushort IeeeLength = 0x008A;    // =138, NOT EtherType (≤ 0x05DC)
        private const byte LlcDsap = 0x03;
        private const byte LlcSsap = 0x03;
        private const byte LlcControlI = 0x08;       // I-frame N(S)=4 — degenerate (constant, no increment)
        private const byte LlcControlS = 0x09;       // S-frame RR — accepted, never observed
        private const byte Magic0 = 0x00;
        private const byte Magic1 = 0xD0;
        private const byte Magic2 = 0x0D;

        /// <summary>
        /// Build a complete 152-byte ACE raw Ethernet frame.
        /// </summary>
        /// <param name="srcMacBytes">Source MAC address, exactly 6 bytes.</param>
        /// <param name="equipmentNumber">Equipment ID (uint32 BE on wire).</param>
        /// <param name="svNumber">Service Vector number (uint32 BE on wire).</param>
        /// <param name="payload124">124-byte signal payload. If null or shorter ⇒ zero-padded; if longer ⇒ trimmed.</param>
        /// <returns>152-byte frame ready for raw socket injection.</returns>
        public static byte[] BuildFrame(byte[] srcMacBytes, uint equipmentNumber, uint svNumber, byte[]? payload124)
        {
            if (srcMacBytes == null || srcMacBytes.Length != 6)
                throw new ArgumentException("srcMacBytes must be exactly 6 bytes", nameof(srcMacBytes));

            var frame = new byte[FrameSize];

            // Dst MAC (6 bytes) — L2 multicast, locally administered
            Buffer.BlockCopy(DstMacBytes, 0, frame, 0, 6);
            // Src MAC (6 bytes)
            Buffer.BlockCopy(srcMacBytes, 0, frame, 6, 6);
            // IEEE 802.3 Length = 0x008A (big-endian: 00 8A on wire)
            frame[0x0C] = 0x00;
            frame[0x0D] = 0x8A;
            // LLC: DSAP=0x03 SSAP=0x03 Control=0x08
            frame[0x0E] = LlcDsap;
            frame[0x0F] = LlcSsap;
            frame[0x10] = LlcControlI;
            // Magic: 00 D0 0D
            frame[0x11] = Magic0;
            frame[0x12] = Magic1;
            frame[0x13] = Magic2;
            // Equipment Number uint32 BE
            frame[0x14] = (byte)((equipmentNumber >> 24) & 0xFF);
            frame[0x15] = (byte)((equipmentNumber >> 16) & 0xFF);
            frame[0x16] = (byte)((equipmentNumber >> 8) & 0xFF);
            frame[0x17] = (byte)(equipmentNumber & 0xFF);
            // Service Vector uint32 BE
            frame[0x18] = (byte)((svNumber >> 24) & 0xFF);
            frame[0x19] = (byte)((svNumber >> 16) & 0xFF);
            frame[0x1A] = (byte)((svNumber >> 8) & 0xFF);
            frame[0x1B] = (byte)(svNumber & 0xFF);
            // Payload: copy up to 124 bytes, remainder stays zero
            if (payload124 != null)
            {
                int copyLen = Math.Min(payload124.Length, PayloadSize);
                Buffer.BlockCopy(payload124, 0, frame, PayloadOffset, copyLen);
            }

            return frame;
        }

        /// <summary>
        /// Validate and parse an ACE frame. Accepts exactly 152-byte frames with correct
        /// dst MAC, magic bytes, and LLC control field (0x08 or 0x09).
        /// </summary>
        /// <param name="frame">Raw frame bytes.</param>
        /// <param name="equipment">Parsed equipment number (uint32 BE).</param>
        /// <param name="sv">Parsed service vector number (uint32 BE).</param>
        /// <param name="payload">Extracted 124-byte payload (new byte array).</param>
        /// <returns>true if frame passes all structural checks.</returns>
        public static bool TryParseFrame(byte[] frame, out uint equipment, out uint sv, out byte[] payload)
        {
            equipment = 0;
            sv = 0;
            payload = Array.Empty<byte>();

            if (frame == null || frame.Length < FrameSize)
                return false;

            // Check dst MAC strictly
            for (int i = 0; i < 6; i++)
            {
                if (frame[i] != DstMacBytes[i])
                    return false;
            }

            // Verify magic bytes at offsets 17,18,19
            if (frame[MagicOffset0] != Magic0 || frame[MagicOffset1] != Magic1 || frame[MagicOffset2] != Magic2)
                return false;

            // Verify LLC: DSAP=0x03, SSAP=0x03, Control ∈ {0x08, 0x09}
            if (frame[LlcOffset] != LlcDsap || frame[LlcOffset + 1] != LlcSsap)
                return false;
            byte ctrl = frame[LlcOffset + 2];
            if (ctrl != LlcControlI && ctrl != LlcControlS)
                return false;

            // Extract equipment number (uint32 BE) from offset 0x14
            equipment = ((uint)frame[0x14] << 24)
                      | ((uint)frame[0x15] << 16)
                      | ((uint)frame[0x16] << 8)
                      | frame[0x17];

            // Extract service vector (uint32 BE) from offset 0x18
            sv = ((uint)frame[0x18] << 24)
               | ((uint)frame[0x19] << 16)
               | ((uint)frame[0x1A] << 8)
               | frame[0x1B];

            // Extract 124-byte payload from offset 0x1C
            payload = new byte[PayloadSize];
            Buffer.BlockCopy(frame, PayloadOffset, payload, 0, PayloadSize);

            return true;
        }

        /// <summary>
        /// Parse a MAC address string (e.g. "03:00:01:00:00:20" or "03-00-01-00-00-20")
        /// into a 6-byte array.
        /// </summary>
        public static byte[] ParseMac(string mac)
        {
            return ParseMacInternal(mac);
        }

        private static byte[] ParseMacInternal(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                throw new ArgumentException("MAC address must not be empty", nameof(mac));

            char separator = mac.Contains(':') ? ':' : '-';
            string[] parts = mac.Split(separator);
            if (parts.Length != 6)
                throw new ArgumentException($"Invalid MAC address format: '{mac}'. Expected 6 hex octets separated by : or -.", nameof(mac));

            var bytes = new byte[6];
            for (int i = 0; i < 6; i++)
                bytes[i] = Convert.ToByte(parts[i].Trim(), 16);
            return bytes;
        }
    }
}
