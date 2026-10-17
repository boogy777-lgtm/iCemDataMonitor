using System.IO;
using System.Threading.Channels;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Telemetry;

/// <summary>
/// Telemetry streaming sampler (D.4). Each subscription is an independent
/// <see cref="MetricsSubscription"/> backed by a capacity-1 bounded channel with
/// <see cref="BoundedChannelFullMode.DropOldest"/>; a slow consumer loses the
/// oldest sample instead of accumulating backlog. The producer reads the shared
/// <see cref="BusReader"/> directly in every iteration (sensor-at-send law:
/// no cache, no pre-filled queue) and emits register deltas relative to the
/// previous sample on the same stream.
/// </summary>
public sealed class MetricsSampler
{
    private const int MinIntervalMs = 200;
    private const int ChannelCapacity = 1;

    private readonly BusReader _reader;

    /// <summary>Creates a sampler bound to the supplied bus reader.</summary>
    public MetricsSampler(BusReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Subscribe to a metric stream. <paramref name="intervalMs"/> is clamped to
    /// a minimum of 200 ms; the resulting sample carries
    /// <see cref="MetricsSample.IntervalClamped"/> when clamping occurred.
    /// </summary>
    /// <param name="intervalMs">Requested sampling interval in milliseconds.</param>
    /// <param name="cancellationToken">Cancels the producer loop.</param>
    /// <returns>A subscription exposing the async stream and per-subscription drop counter.</returns>
    public MetricsSubscription Subscribe(int intervalMs, CancellationToken cancellationToken = default)
    {
        bool clamped = intervalMs < MinIntervalMs;
        int effectiveInterval = clamped ? MinIntervalMs : intervalMs;

        var channel = Channel.CreateBounded<MetricsSample>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var subscription = new MetricsSubscription(channel.Reader);
        // NOTE: the cancellation token is deliberately NOT passed to Task.Run.
        // A pre-cancelled token would prevent the delegate from ever running,
        // leaving the channel un-completed and the consumer awaiting forever.
        // The loop checks the token itself and always completes the channel
        // (termination law: every stream ends, empty or not).
        subscription.ProducerCompletion = Task.Run(
            () => ProducerLoopAsync(channel, effectiveInterval, clamped, subscription, cancellationToken));
        return subscription;
    }

    private async Task ProducerLoopAsync(
        Channel<MetricsSample> channel,
        int intervalMs,
        bool intervalClamped,
        MetricsSubscription subscription,
        CancellationToken ct)
    {
        var writer = channel.Writer;
        try
        {
            ushort[]? previous = null;

            while (!ct.IsCancellationRequested)
            {
                var sample = BuildSample(ref previous, intervalClamped);

                // BoundedChannel with DropOldest never fails TryWrite, but if the
                // channel is at capacity the oldest sample is evicted. Approximate the
                // drop count by inspecting the reader count before writing.
                if (channel.Reader.Count >= ChannelCapacity)
                {
                    Interlocked.Increment(ref subscription._dropped);
                }

                if (!writer.TryWrite(sample))
                {
                    // Channel completed externally (reader disposed); stop gracefully.
                    break;
                }

                try
                {
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }

        writer.TryComplete();
    }

    private MetricsSample BuildSample(ref ushort[]? previous, bool intervalClamped)
    {
        // Sensor-at-send law: read the bus directly in the iteration that emits
        // the sample. No cache, no pre-filled queue.
        var snap = _reader.Snapshot();
        if (snap is null || !_reader.IsOpen)
        {
            // Fail-fast: a telemetry stream without a live bus is useless. Do not
            // emit silent fake samples (e.g. heartbeat=-1 or empty deltas).
            throw new IOException(
                "Telemetry stream cannot continue: the bus is not open or no snapshot is available " +
                $"(reader state: {_reader.LastError ?? "unknown"}). " +
                "Ensure the iCem AddIn has created the bus file.");
        }

        var sample = new MetricsSample
        {
            TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Heartbeat = snap.Heartbeat,
            ComPortActive = snap.ComPort > 0,
            CaseState = snap.CaseState,
            IntervalClamped = intervalClamped,
        };

        ReadOnlySpan<ushort> words = snap.Words.AsSpan();
        var deltas = ComputeDeltas(words, previous);
        sample.Deltas.AddRange(deltas);

        previous = snap.Words.ToArray();
        return sample;
    }

    /// <summary>
    /// Live bus registers are absolute addresses 0..599 (v5 header state regs 0..3
    /// plus data zones 4..199 / 200..399 / 400..599). The first sample on a new
    /// subscription carries all live registers; subsequent samples carry only
    /// registers whose value changed since the previous sample on THIS stream.
    /// </summary>
    private static List<RegisterDelta> ComputeDeltas(ReadOnlySpan<ushort> words, ushort[]? previous)
    {
        var deltas = new List<RegisterDelta>();

        // Header state registers 0..3 are single u16 words surfaced as int32.
        for (int addr = 0; addr < BusLayout.HeaderRegCount; addr++)
        {
            ushort value = addr < words.Length ? words[addr] : (ushort)0;
            if (previous is null || previous.Length <= addr || previous[addr] != value)
            {
                deltas.Add(new RegisterDelta { Addr = (uint)addr, Int32Value = value });
            }
        }

        // Live zone CDAB pairs 4..599. Addr 4..5 carry Unix timestamp int32 raw bits;
        // all other pairs are float32 values.
        for (int addr = BusLayout.HeaderRegCount;
             addr + 1 < RegisterDecoder.LiveEndExclusive && addr + 1 < words.Length;
             addr += 2)
        {
            bool changed = previous is null
                || addr >= previous.Length
                || previous[addr] != words[addr]
                || previous[addr + 1] != words[addr + 1];

            if (changed)
            {
                deltas.Add(CreatePairDelta(words, addr));
            }
        }

        return deltas;
    }

    private static RegisterDelta CreatePairDelta(ReadOnlySpan<ushort> words, int addr)
    {
        // Decode law lives in ONE place (W20 F.1): RegisterDecoder serves both
        // this streaming projection and the unary GetRegisters projection.
        var (isInt32, intValue, floatValue) = RegisterDecoder.DecodePair(words, addr);
        return isInt32
            ? new RegisterDelta { Addr = (uint)addr, Int32Value = intValue }
            : new RegisterDelta { Addr = (uint)addr, FloatValue = floatValue };
    }
}

/// <summary>
/// One telemetry subscription. Exposes the metric stream as an
/// <see cref="IAsyncEnumerable{T}"/> and reports how many samples were dropped
/// because the consumer could not keep up. The counter is per-subscription;
/// there is intentionally no field for it in <see cref="MetricsSample"/>
/// because the contract .proto is frozen.
/// </summary>
public sealed class MetricsSubscription : IAsyncEnumerable<MetricsSample>
{
    private readonly ChannelReader<MetricsSample> _reader;
    internal long _dropped;

    // Producer completion is distinct from ChannelReader.Completion, which
    // also waits for buffered samples to be consumed. Tests stop and join the
    // writer before checking exactly what the bounded channel retained.
    internal Task ProducerCompletion { get; set; } = Task.CompletedTask;

    internal MetricsSubscription(ChannelReader<MetricsSample> reader)
    {
        _reader = reader;
    }

    /// <summary>Number of samples dropped due to a full channel (slow consumer).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public IAsyncEnumerator<MetricsSample> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
        => _reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
}
