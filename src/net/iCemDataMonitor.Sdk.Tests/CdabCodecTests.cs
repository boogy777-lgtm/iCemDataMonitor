using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Sdk.Tests;

// W1 contract tests: CDAB float32 encoding, pinned against the production encoding
// in iCemDataMonitor.Transport\ModbusTcpPublisher.cs (reg[N] = bits & 0xFFFF, reg[N+1] = bits >> 16).
public class CdabCodecTests
{
    [Fact]
    public void Split_1_0f_Is_0000_3F80()
    {
        var (low, high) = CdabCodec.Split(1.0f);
        Assert.Equal(0x0000, low);
        Assert.Equal(0x3F80, high);
    }

    [Fact]
    public void Split_15_79f_Is_A3D7_417C()
    {
        var (low, high) = CdabCodec.Split(15.79f);
        Assert.Equal(0xA3D7, low);
        Assert.Equal(0x417C, high);
    }

    [Fact]
    public void Combine_0000_3F80_Is_1_0f()
    {
        Assert.Equal(1.0f, CdabCodec.Combine(0x0000, 0x3F80));
    }

    [Fact]
    public void Combine_A3D7_417C_Is_15_79f()
    {
        Assert.Equal(15.79f, CdabCodec.Combine(0xA3D7, 0x417C));
    }

    [Fact]
    public void Roundtrip_Preserves_Bits_Exactly()
    {
        float[] values = { 0.0f, -0.0f, 1.0f, -1.0f, 3.5f, -15.79f, 123456.789f,
                           1.4012984643E-45f /*float.Epsilon*/, float.MaxValue, float.NaN };
        foreach (var v in values)
        {
            var (low, high) = CdabCodec.Split(v);
            var back = CdabCodec.Combine(low, high);
            Assert.Equal(BitConverter.SingleToUInt32Bits(v), BitConverter.SingleToUInt32Bits(back));
        }
    }

    [Fact]
    public void Split_Matches_ModbusTcpPublisher_Encoding_Formula()
    {
        // Production pin (ModbusTcpPublisher.RefreshRegistersAsync):
        //   uint bits = BitConverter.SingleToUInt32Bits((float)val);
        //   reg[N]   = (ushort)(bits & 0xFFFF);
        //   reg[N+1] = (ushort)((bits >> 16) & 0xFFFF);
        foreach (var v in new[] { 1.0f, 15.79f, -2.5f, 0.0f, 42.25f, 3.14159f })
        {
            uint bits = BitConverter.SingleToUInt32Bits(v);
            var (low, high) = CdabCodec.Split(v);
            Assert.Equal((ushort)(bits & 0xFFFF), low);
            Assert.Equal((ushort)((bits >> 16) & 0xFFFF), high);
        }
    }

    [Fact]
    public void Split_Is_Deterministic_And_Words_Are_Independent()
    {
        // Low word alone carries bytes C,D; high word alone carries bytes A,B.
        // For 15.79f: full bits 0x417CA3D7 → reg[N]=0xA3D7 (C,D), reg[N+1]=0x417C (A,B).
        uint bits = BitConverter.SingleToUInt32Bits(15.79f);
        Assert.Equal(0x417CA3D7u, bits);
        Assert.Equal((ushort)0xA3D7, (ushort)(bits & 0xFFFF));
        Assert.Equal((ushort)0x417C, (ushort)(bits >> 16));
    }
}
