// IDfbBlock + DfbBlockBase — контракт блока (IEC 61131-3 Derived Function Block).
// Входы/выходы = DfbSignal; Execute(ctx) читает входы, пишет выходы, хранит состояние.

namespace Halliburton.iCemEmule.Dfb;

public interface IDfbBlock
{
    string Name { get; }
    IReadOnlyList<DfbSignal> Inputs { get; }
    IReadOnlyList<DfbSignal> Outputs { get; }
    void Execute(DfbContext ctx);
}

public abstract class DfbBlockBase : IDfbBlock
{
    public string Name { get; }
    public IReadOnlyList<DfbSignal> Inputs { get; protected set; } = Array.Empty<DfbSignal>();
    public IReadOnlyList<DfbSignal> Outputs { get; protected set; } = Array.Empty<DfbSignal>();

    protected DfbBlockBase(string name) => Name = name;

    public abstract void Execute(DfbContext ctx);
}
