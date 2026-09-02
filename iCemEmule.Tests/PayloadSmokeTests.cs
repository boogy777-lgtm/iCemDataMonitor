// PayloadSmokeTests — интеграция DFB → payload (EXEC_DFBENGINE.verify: smoke):
// GetSuperframe после прогрева ⇒ Lan/* сигналы ≠ 0; kickout виден в байтах SV 0x13120206.

using Halliburton.iCemEmule.Dfb;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace DfbEngine.Tests;

public sealed class PayloadSmokeTests
{
    private static (DfbNetwork net, DfbAcePayloadProvider provider) Build()
    {
        var net = CementUnitNetworkFactory.Create();
        var baseProvider = new AcePayloadProvider();
        var source = new DfbSignalSource(net);
        var provider = new DfbAcePayloadProvider(baseProvider, new DfbSignalSourceAdapter(source));
        return (net, provider);
    }

    // Закон раскладки (реальный cRIO, подтверждён wire-дампами): сигнал живёт на
    // payload[ByteOffset − 4]. Движок (DfbAcePayloadProvider.Patch) кладёт байты
    // именно туда — читать надо по тому же закону, НЕ по sig.ByteOffset.
    private static float ReadFloat(byte[] payload, LantableRegistry.SignalDef sig)
    {
        var bytes = new byte[4];
        Array.Copy(payload, sig.ByteOffset - 4, bytes, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    private static uint ReadUInt(byte[] payload, LantableRegistry.SignalDef sig)
    {
        var bytes = new byte[4];
        Array.Copy(payload, sig.ByteOffset - 4, bytes, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static LantableRegistry.SignalDef Sig(LantableRegistry.ServiceVectorDef sv, string name)
        => sv.Signals.First(s => s.Name == name);

    [Fact]
    public void Superframe_has_live_pump_pressure_and_density_after_warmup()
    {
        var (net, provider) = Build();
        var lantable = new LantableRegistry();

        // прогрев 30 тиков (1 Hz)
        for (int t = 1; t <= 30; t++)
            net.Execute(DfbContext.Create(t, t, DfbEngineBridge.Seed));

        var sf = provider.GetSuperframe(0x00A86F46, 30.0);

        var pumpSv = sf.First(v => v.SvNumber == 0x13120206);
        var pumpDef = lantable.GetBySvNumber(0x13120206)!;
        float pA = ReadFloat(pumpSv.Payload, Sig(pumpDef, "Pump A Pressure"));
        Assert.InRange(pA, 1100f, 1300f);   // 3 bpm · 400 psi/bpm

        uint ko = ReadUInt(pumpSv.Payload, Sig(pumpDef, "Side A Kicked Out?"));
        Assert.Equal(0u, ko);               // штатно — без срабатывания

        var mixSv = sf.First(v => v.SvNumber == 0x13120200);
        var mixDef = lantable.GetBySvNumber(0x13120200)!;
        float density = ReadFloat(mixSv.Payload, Sig(mixDef, "Recirc Density"));
        Assert.InRange(density, 15.0f, 16.2f);   // лаг к BlendTarget 15.8

        float mixRate = ReadFloat(mixSv.Payload, Sig(mixDef, "Mix Water Rate"));
        Assert.InRange(mixRate, 0.5f, 1.8f);     // 2.1 bpm · 60% · jobActive
    }

    [Fact]
    public void Kickout_trip_is_visible_in_payload_bytes()
    {
        var (net, provider) = Build();
        var lantable = new LantableRegistry();

        // перегрузка насоса A
        net.Signal<double>("PumpA.SetpointRate").Value = 10.0;
        net.Signal<double>("PumpA.OpkoSetting").Value = 3000.0;

        for (int t = 1; t <= 60; t++)
            net.Execute(DfbContext.Create(t, t, DfbEngineBridge.Seed));

        var sf = provider.GetSuperframe(0x00A86F46, 60.0);
        var pumpSv = sf.First(v => v.SvNumber == 0x13120206);
        var pumpDef = lantable.GetBySvNumber(0x13120206)!;

        uint koA = ReadUInt(pumpSv.Payload, Sig(pumpDef, "Side A Kicked Out?"));
        Assert.Equal(1u, koA);

        float koPress = ReadFloat(pumpSv.Payload, Sig(pumpDef, "Side A Pressure At KO"));
        Assert.True(koPress >= 3000.0f, $"Pressure At KO = {koPress} < 3000");

        uint koB = ReadUInt(pumpSv.Payload, Sig(pumpDef, "Side B Kicked Out?"));
        Assert.Equal(0u, koB);   // сторона B не задета
    }

    [Fact]
    public void Fallback_keeps_payload_identical_when_dfb_silent()
    {
        // DFB-источник возвращает false для всех имён ⇒ декоратор не трогает байты
        var baseProvider = new AcePayloadProvider();
        var silent = new SilentValueProvider();
        var provider = new DfbAcePayloadProvider(baseProvider, silent);

        var expected = baseProvider.GetSuperframe(0x00A86F46, 5.0);
        var actual = provider.GetSuperframe(0x00A86F46, 5.0);

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].SvNumber, actual[i].SvNumber);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
        }
    }

    private sealed class SilentValueProvider : ISignalValueProvider
    {
        public bool TryGetValue(string wireName, out double value) { value = 0.0; return false; }
        public double GetValue(string wireName) => 0.0;
    }
}
