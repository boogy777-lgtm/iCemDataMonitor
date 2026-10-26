using System.Collections;

namespace iCemDataMonitor.Sdk.Collections;

/// <summary>
/// An owned, read-only copy of list storage. Elements are copied as values;
/// referenced objects are not cloned. No mutable collection or memory view is exposed.
/// </summary>
public sealed class OwnedReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly T[] _items;

    public OwnedReadOnlyList(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = new T[items.Count];
        // Caller-defined ICollection.CopyTo must never receive our backing array.
        for (int i = 0; i < _items.Length; i++)
            _items[i] = items[i];
    }

    public int Count => _items.Length;
    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _items.Length; i++)
            yield return _items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
