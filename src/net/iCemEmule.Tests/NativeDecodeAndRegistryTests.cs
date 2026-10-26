using System.Reflection;
using System.Text.Json;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace iCemEmule.Tests;

public sealed class NativeDecodeAndRegistryTests
{
    [Theory]
    [InlineData("UL", 1, 255)] [InlineData("UI", 2, 65535)]
    [InlineData("HX", 4, 4294967295d)] [InlineData("UL", 8, 18446744073709551615d)]
    [InlineData("SI", 1, -1)] [InlineData("SL", 2, -1)]
    [InlineData("SI", 4, -1)] [InlineData("SL", 8, -1)]
    public void IntegerWidthsSignednessAndFactor(string type, int width, double raw)
    {
        var bytes = Enumerable.Repeat((byte)255, width).ToArray();
        var sig = new LantableRegistry.SignalDef(4, width, type, 0.5, "v", "");
        Assert.True(SignalWireCodec.TryDecodeNative(bytes, sig, out var value));
        Assert.Equal(raw * 0.5, value);
        Assert.False(SignalWireCodec.TryDecodeNative(bytes, sig with { ByteOffset = 5 }, out _));
    }

    [Fact]
    public void FloatSpecialBitsAndUnsupportedDefinitions()
    {
        var sig = new LantableRegistry.SignalDef(4, 4, "FF", 99, "v", "");
        Assert.True(SignalWireCodec.TryDecodeNative(new byte[] { 128, 0, 0, 0 }, sig, out var zero));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(zero));
        Assert.True(SignalWireCodec.TryDecodeNative(new byte[] { 127, 192, 0, 0 }, sig, out var nan));
        Assert.True(double.IsNaN(nan));
        foreach (var bad in new[] { sig with { Size = 2 }, sig with { ByteOffset = 3 }, sig with { ValueType = "UC" }, sig with { ValueType = "?" }, sig with { Size = 3, ValueType = "UL" } })
            Assert.False(SignalWireCodec.TryDecodeNative(new byte[8], bad, out _));
    }

    [Fact]
    public void Loader_ParsesDuplicateBeforeDiscarding_KeepsPartialRows_ThenRecovers()
    {
        var registry = new LantableRegistry();
        var definitions = (Dictionary<uint, LantableRegistry.ServiceVectorDef>)registry.All;
        definitions.Clear();
        var file = "c9_parser_" + Guid.NewGuid().ToString("N") + ".json";
        var path = Path.Combine(AppContext.BaseDirectory, "templates", file);
        var load = typeof(LantableRegistry).GetMethod("LoadFile", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            File.WriteAllText(path, """{"serviceVectors":[{"svNumber":1,"name":null,"signals":[{"byteOffset":4,"size":4,"valueType":"FF","name":null}]},{"svNumber":1,"name":"duplicate","signals":[{}]},{"svNumber":2,"name":"unreached"}]}""");
            load.Invoke(registry, new object[] { file });
            Assert.Single(definitions);
            var first = definitions[1];
            Assert.Equal("", first.Name);
            Assert.Equal("", first.EquipmentType);
            Assert.Equal(1, first.Signals[0].DasFactor);
            Assert.Equal("", first.Signals[0].UoM);
            File.WriteAllText(path, """{"serviceVectors":[{"svNumber":1,"name":"duplicate"},{"svNumber":2,"name":"recovered"}]}""");
            load.Invoke(registry, new object[] { file });
            Assert.Equal("", definitions[1].Name);
            Assert.Equal("recovered", definitions[2].Name);
            load.Invoke(registry, new object[] { "missing_" + file });
            Assert.Equal(2, definitions.Count);
        }
        finally { File.Delete(path); }
    }
}
