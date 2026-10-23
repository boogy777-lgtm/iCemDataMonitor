using System.Buffers.Binary;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace iCemEmule.Tests;

public sealed class EmulatorStepTests
{
    private sealed class RecordingProvider : IAcePayloadProvider
    {
        internal readonly List<double> Elapsed = new();
        internal bool Fail;
        public IReadOnlyList<uint> ActiveSvNumbers => Array.Empty<uint>();
        public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
        {
            Elapsed.Add(elapsedSeconds);
            if (Fail) throw new InvalidOperationException("provider failure");
            return Array.Empty<AceVectorData>();
        }
    }

    [Fact]
    public void ActualPreparation_BusyEdgeFailureAndRetry_AdvanceExactlyOnce()
    {
        var provider = new RecordingProvider();
        var step = new EmulatorStep(new SimulationEngine(), new AcePayloadProvider(), null, UnitPreset.LanNative, provider);
        Assert.NotNull(step.PrepareLan(true, false, 7));
        Assert.NotNull(step.PrepareLan(true, false, 7));
        Assert.Null(step.PrepareLan(false, true, 7));
        Assert.Null(step.PrepareLan(true, true, 7)); // reset must happen even while sending
        Assert.Equal(0, step.LanElapsed);
        provider.Fail = true;
        Assert.Null(step.PrepareLan(true, false, 7));
        Assert.Equal(1, step.LanElapsed);
        provider.Fail = false;
        Assert.NotNull(step.PrepareLan(true, false, 7));
        Assert.Equal(new double[] { 0, 1, 0, 1 }, provider.Elapsed);
    }

    [Fact]
    public void ActualComPreparation_SeedFreezeManualUnitsAndFreshArrayCacheAlias()
    {
        var engine = new SimulationEngine();
        var step = new EmulatorStep(engine, new AcePayloadProvider(), null, UnitPreset.Metric)
        { Units = new UnitSettings { Preset = UnitPreset.Metric } };
        var first = step.PrepareCom();
        var seededCache = engine.LastValues;
        Assert.NotEmpty(seededCache);
        step.PrepareCom();
        Assert.Equal(seededCache, engine.LastValues); // frozen conversion works on a clone
        var index = Array.FindIndex(engine.Current!.Variables, v => UnitConversion.IsConvertible(v.UoM));
        Assert.True(index >= 0);
        var variable = engine.Current.Variables[index];
        engine.SetOverride(variable.Name, 1234);
        var frozenManual = step.PrepareCom();
        Assert.Equal(seededCache, engine.LastValues);
        step.ComEmulating = true;
        step.PrepareCom();
        Assert.Equal(UnitConversion.ToTarget(1234, variable.UoM, step.Units), engine.LastValues[index]);
        Assert.NotNull(first);
        Assert.NotNull(frozenManual);
    }

    [Fact]
    public void ActualLanPreparation_DfbFreezeManualThenUnits()
    {
        var step = new EmulatorStep(UnitPreset.LanNative);
        var registry = new LantableRegistry();
        const uint sv = 0x13120209;
        var sig = registry.GetBySvNumber(sv)!.Signals.First(s => s.Name == "Count Input 1");
        double Read(PreparedLanStep prepared)
        {
            Assert.True(SignalWireCodec.TryDecodeNative(prepared.Vectors.First(v => v.SvNumber == sv).Payload, sig, out var value));
            return value;
        }
        step.LanEmulating = true;
        step.FrozenProvider.Frozen = false;
        Assert.Equal(0, Read(step.PrepareLan(true, false, 0x00A86F46)!));
        Assert.Equal(10, Read(step.PrepareLan(true, false, 0x00A86F46)!));
        step.LanEmulating = false;
        step.FrozenProvider.Frozen = true;
        Assert.Equal(10, Read(step.PrepareLan(true, false, 0x00A86F46)!));
        step.OverrideProvider.SetOverride(sv, sig.Name, 12345);
        Assert.Equal(12345, Read(step.PrepareLan(true, false, 0x00A86F46)!));
        var temperature = registry.GetBySvNumber(sv)!.Signals.First(s => s.Name == "Display Temperature");
        step.OverrideProvider.SetOverride(sv, temperature.Name, 212);
        step.Units = new UnitSettings { Preset = UnitPreset.Metric };
        step.LanUnitProvider.Settings = step.Units;
        var metric = step.PrepareLan(true, false, 0x00A86F46)!;
        Assert.True(SignalWireCodec.TryDecodeNative(metric.Vectors.First(v => v.SvNumber == sv).Payload, temperature, out var converted));
        Assert.Equal(100, converted, 4);
        var native = step.OverrideProvider.GetSuperframe(0x00A86F46, step.LanElapsed);
        Assert.True(SignalWireCodec.TryDecodeNative(native.First(v => v.SvNumber == sv).Payload, temperature, out var raw));
        Assert.Equal(212, raw); // control get/vars remain before units
    }
}
