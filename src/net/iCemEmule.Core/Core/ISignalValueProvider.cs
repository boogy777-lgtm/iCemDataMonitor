// ISignalValueProvider — additive contract (INT_DfbEngine, ADR_001).
// Поставщик доменных значений по wireName (families.lan.wireName из variable_registry.json).
// Позволяет DFB-движку оживлять сигналы без изменения frozen AcePayloadProvider.cs.

namespace Halliburton.iCemEmule.Core;

public interface ISignalValueProvider
{
    bool TryGetValue(string wireName, out double value);
    double GetValue(string wireName);
}
