// DfbSignal — типизированный порт блока. Read() следует за проводкой ConnectedFrom.

namespace Halliburton.iCemEmule.Dfb;

public abstract class DfbSignal
{
    public string Path { get; }
    public DfbSignal? ConnectedFrom { get; set; }

    protected DfbSignal(string path) => Path = path;
}

public sealed class DfbSignal<T> : DfbSignal
{
    public T Value { get; set; } = default!;

    public DfbSignal(string path) : base(path) { }

    public T Read() => ConnectedFrom is DfbSignal<T> wired ? wired.Value : Value;
}
