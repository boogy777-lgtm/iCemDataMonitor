// FrozenPayloadProvider (W14) — emulation freeze gate for the LAN chain
// (flecsEmu "Emule" checkbox, headless edition). Frozen = ON: returns the LAST
// superframe captured from the inner provider instead of regenerating — the
// simulator stops driving the wire, so manually written values are NOT
// overwritten by generated data (old FlecsEmu bug: emulated values kept
// flowing with emulation off). Downstream decorators (manual overrides, unit
// conversion) still apply on every tick — `set`/`clear` stay live while frozen.
// Sits BETWEEN the DFB decorator and ManualOverridePayloadProvider:
//   base → DFB → FROZEN → overrides → units

namespace Halliburton.iCemEmule.Core;

public sealed class FrozenPayloadProvider : IAcePayloadProvider
{
    private readonly IAcePayloadProvider _inner;
    private IReadOnlyList<AceVectorData>? _last;

    /// <summary>true = hold the last generated superframe; false = pass through (and recache).</summary>
    public bool Frozen { get; set; }

    public FrozenPayloadProvider(IAcePayloadProvider inner) => _inner = inner;

    public IReadOnlyList<uint> ActiveSvNumbers => _inner.ActiveSvNumbers;

    public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
    {
        if (Frozen && _last is not null)
            return _last;   // ManualOverridePayloadProvider patches CLONED payloads — cache stays pristine
        var vectors = _inner.GetSuperframe(equipmentNumber, elapsedSeconds);
        _last = vectors;
        return vectors;
    }
}
