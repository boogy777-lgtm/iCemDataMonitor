// DfbNetwork — контейнер блоков с топологическим порядком (O(V+E) на Add).
// Порядок: новый блок вставляется после последнего продюсера своих входов.
// Execute под lock; поиск сигнала по path — O(1).

namespace Halliburton.iCemEmule.Dfb;

public sealed class DfbNetwork
{
    private readonly object _lock = new();
    private readonly List<IDfbBlock> _blocks = new();
    private readonly Dictionary<string, IDfbBlock> _producers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DfbSignal> _signals = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IDfbBlock> Blocks
    {
        get { lock (_lock) return _blocks.ToArray(); }
    }

    public void Add(IDfbBlock block)
    {
        lock (_lock)
        {
            int insertAt = _blocks.Count;

            foreach (var input in block.Inputs)
            {
                _signals[input.Path] = input;
                string sourcePath = input.ConnectedFrom?.Path ?? input.Path;
                if (_producers.TryGetValue(sourcePath, out var producer))
                {
                    int pos = _blocks.IndexOf(producer);
                    if (pos >= 0 && pos >= insertAt) insertAt = pos + 1;
                }
            }

            foreach (var output in block.Outputs)
            {
                _producers[output.Path] = block;
                _signals[output.Path] = output;
            }

            _blocks.Insert(insertAt, block);
        }
    }

    public void Execute(DfbContext ctx)
    {
        IDfbBlock[] snapshot;
        lock (_lock) snapshot = _blocks.ToArray();
        foreach (var b in snapshot) b.Execute(ctx);
    }

    public DfbSignal<T> Signal<T>(string path)
    {
        lock (_lock)
        {
            if (_signals.TryGetValue(path, out var s) && s is DfbSignal<T> typed)
                return typed;
        }
        throw new InvalidOperationException($"DFB signal not found: '{path}'");
    }

    public bool TryFindSignal<T>(string path, out DfbSignal<T>? signal)
    {
        lock (_lock)
        {
            if (_signals.TryGetValue(path, out var s) && s is DfbSignal<T> typed)
            {
                signal = typed;
                return true;
            }
        }
        signal = null;
        return false;
    }
}
