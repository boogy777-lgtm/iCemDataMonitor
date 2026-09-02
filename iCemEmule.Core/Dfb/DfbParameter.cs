// DfbParameter — конфигурация блока (константа на время работы сети).

namespace Halliburton.iCemEmule.Dfb;

public sealed class DfbParameter<T>
{
    public T Value { get; set; }

    public DfbParameter(T value) => Value = value;
}
