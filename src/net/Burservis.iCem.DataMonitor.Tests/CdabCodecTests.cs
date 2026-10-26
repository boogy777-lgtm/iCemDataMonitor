using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

public class CdabCodecTests
{
    [Theory]
    // Contract pins (CONTRACT_BusV2.yaml §4): reg[N]=low word (C,D), reg[N+1]=high word (A,B).
    [InlineData(1.0f, 0x0000, 0x3F80)]
    [InlineData(15.79f, 0xA3D7, 0x417C)]
    [InlineData(0.0f, 0x0000, 0x0000)]
    [InlineData(-12.5f, 0x0000, 0xC148)]
    [InlineData(2.5f, 0x0000, 0x4020)]
    [InlineData(float.MaxValue, 0xFFFF, 0x7F7F)]
    [InlineData(float.Epsilon, 0x0001, 0x0000)]
    public void Split_ProducesPinnedWords(float value, ushort expectedLow, ushort expectedHigh)
    {
        ushort low, high;
        CdabCodec.Split(value, out low, out high);

        Assert.Equal(expectedLow, low);
        Assert.Equal(expectedHigh, high);
    }

    [Theory]
    [InlineData(0x0000, 0x3F80, 1.0f)]
    [InlineData(0xA3D7, 0x417C, 15.79f)]
    [InlineData(0x0000, 0xC148, -12.5f)]
    public void Combine_ProducesPinnedFloats(ushort low, ushort high, float expected)
    {
        float actual = CdabCodec.Combine(low, high);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(3.1415927f)]
    [InlineData(-9876.543f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void SplitCombine_RoundTrips(float value)
    {
        ushort low, high;
        CdabCodec.Split(value, out low, out high);
        float back = CdabCodec.Combine(low, high);

        if (float.IsNaN(value))
        {
            Assert.True(float.IsNaN(back));
        }
        else
        {
            Assert.Equal(value, back);
        }
    }
}
