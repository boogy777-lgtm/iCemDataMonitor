using System.Threading.Channels;
using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.Host.Boot;

/// <summary>
/// W21.3: process-wide recorder of boot stages. Created BEFORE any startup
/// work in ProgramMain (so even pre-Kestrel stages — ini, composition — are
/// captured) and registered AS THE SAME INSTANCE in DI by HostAppFactory.Build,
/// where TelemetryServiceImpl.SubscribeBootStages picks it up.
///
/// Contract semantics (telemetry.proto W21.1): stages accumulate from PROCESS
/// START; subscribers first receive a REPLAY of everything recorded so far,
/// then live updates. A stage begins unchecked (Begin) and flips its checkbox
/// on Complete. A failed boot leaves the failing stage unchecked — honest
/// splash, ¬fake progress.
///
/// Scale note: a boot has ~6 stages and O(10) updates, so an unbounded
/// per-subscriber channel plus a capped stage list IS the ring buffer here —
/// no eviction logic is needed at this cardinality (YAGNI guard).
/// </summary>
public sealed class BootStageTracker
{
    private const int MaxStages = 64;

    private readonly object _lock = new();
    private readonly List<BootStage> _stages = new();
    private readonly List<Channel<BootStage>> _subscribers = new();
    private uint _nextOrdinal = 1;

    /// <summary>Open (or re-open) a stage: visible immediately, checkbox off.</summary>
    public void Begin(string id, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Upsert(id, title, completed: false, detail: null);
    }

    /// <summary>Check the checkbox. Unknown id ⇒ fail-loud (a mistyped stage id
    /// is a programming defect, not a runtime condition).</summary>
    public void Complete(string id, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_lock)
        {
            var stage = FindLocked(id);
            if (stage is null)
                throw new InvalidOperationException($"boot stage '{id}' was never begun");
            stage.Completed = true;
            if (detail is not null)
                stage.Detail = detail;
            PublishLocked(stage);
        }
    }

    /// <summary>Immutable-ish snapshot for diagnostics and stream replay.</summary>
    public IReadOnlyList<BootStage> Snapshot()
    {
        lock (_lock)
            return _stages.Select(s => s.Clone()).ToList();
    }

    /// <summary>
    /// Subscribe: the returned reader first yields the full snapshot REPLAY,
    /// then live updates until <paramref name="ct"/> fires. Usage:
    /// <c>await foreach (var s in tracker.Subscribe(ct).ReadAllAsync(ct))</c>.
    /// </summary>
    public ChannelReader<BootStage> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<BootStage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_lock)
        {
            foreach (var s in _stages)
                channel.Writer.TryWrite(s.Clone());
            _subscribers.Add(channel);
        }
        ct.Register(() => RemoveSubscriber(channel));
        return channel.Reader;
    }

    private void Upsert(string id, string title, bool completed, string? detail)
    {
        lock (_lock)
        {
            var stage = FindLocked(id);
            if (stage is null)
            {
                if (_stages.Count >= MaxStages)
                    throw new InvalidOperationException(
                        $"boot stage table overflow (> {MaxStages}) — defect, not a runtime condition");
                stage = new BootStage { Ordinal = _nextOrdinal++, Id = id, Title = title };
                _stages.Add(stage);
            }
            stage.Completed = completed;
            if (detail is not null)
                stage.Detail = detail;
            PublishLocked(stage);
        }
    }

    private BootStage? FindLocked(string id) =>
        _stages.FirstOrDefault(s => s.Id == id);

    /// <summary>Publish a CLONE — the stored instance keeps mutating, streams don't.</summary>
    private void PublishLocked(BootStage stage)
    {
        foreach (var sub in _subscribers)
            sub.Writer.TryWrite(stage.Clone());
    }

    private void RemoveSubscriber(Channel<BootStage> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }
}
