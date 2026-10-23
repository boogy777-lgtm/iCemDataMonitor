using System.Net;
using System.Net.Sockets;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Modbus TCP slave — TZ_ARCH_V2 §5 layers.modbus, CONTRACT_BusV2 (W3).
///
/// Register file (layout v5 — RUNTIME-SIZED, dynamic-regCount law preserved):
/// the file is NOT a hardcoded map. It is sized from the RUNTIME bus dictionary:
///   • before the first snapshot: options.Total (ini total, default
///     BusLayout.RegCount — the configured dictionary);
///   • on every FRESH snapshot: re-shaped to snap.RegCount — the bus header is
///     self-describing (regCount @8..9, 650 ≤ N ≤ 65535, len == 64+2N) and the
///     bus is the source of truth. Grow = zero-fill, shrink = truncate.
///   • stale snapshot ⇒ no re-shape (last-good hold, AC3/AC4).
/// FC03/FC04/FC06/FC16 bounds are validated against the CURRENT register file
/// size, so the map edge moves with the bus.
///
/// ALL registers are readable (FC03/FC04) and writable (FC06/FC16). The bus
/// body is a byte-exact copy of the Modbus-wire image (CONTRACT_BusV2 §3
/// memcpy_law) ⇒ each tick performs a pure WORD memcpy from the bus
/// snapshot into the register file — ZERO float work, ZERO re-encode. Tick
/// cadence = options.TickMs (station.ini modbus_tick_ms, PLC range
/// [200..500], default 500) — EXTERNAL config, operator directive. The tick
/// is a PLAIN OVERWRITE every cycle: Δ-based change detection is BANNED.
///
/// Tick semantics (axioms A4/A5 + §5 write_conflict, layout v5):
///   regs 0..3 (header STATE block: comPortActive, caseState, caseLanState,
///   heartbeat@3 — ALL AddIn-owned, v5) are NOT inside any zone. The
///   publisher ALWAYS mirrors them from the bus snapshot each tick — the copy
///   length is BusLayout.HeaderRegCount (4 in v5, auto-follows the const), and
///   the words are written by their owner (AddIn) into the BUS and flow
///   through verbatim. Bus file absent and no snapshot ever taken ⇒ zeros.
///   ACTIVE MIRROR ZONE semantics: external FC06/FC16 writes into 0..3 land
///   in the register file (A4: freely writable) but are overwritten by the
///   mirror on the next tick — the A5 hold rule of inactive zones applies to
///   ZONES only. v5 has NO state/zone overlap: ZoneBaseMin=4, com_stream
///   starts at reg 4 — regs 4..15 are com_stream float pairs now, governed by
///   the com zone flag (the v4 4..15 overlap/skip rule is GONE).
///   zone activeFlag=1 ⇒ zone words COPIED from the bus snapshot (overwrite).
///   An external write into an ACTIVE zone survives ≤ one tick interval
///   (options.TickMs), then the bus
///   (source of truth) wins — operator-approved race verdict (AC5,
///   CONTRACT_BusV2 §6 race_verdict).
///   zone activeFlag=0 ⇒ zone NOT touched ⇒ external FC06/FC16 writes persist
///   indefinitely (A5 — spec'd edge-case logic, ¬ a bug).
///   bus absent (TryOpen fails) or snapshot stale (mutex timeout ⇒
///   BusSnapshot.Stale) ⇒ NO zone overwrite; registers hold; server keeps
///   answering (stale-hold per AC3/AC4).
///
/// Zone enumeration is DATA-DRIVEN: the tick iterates the v5 mirror table
/// (com_stream 4..199, lan_stream_crio 200..399, raw_com_line 400..599 —
/// bounds from the BusLayout v5 zone consts; ALL zones AddIn-owned in v5).
/// Each zone is mirrored by its own activeFlag; the Lan liveness block
/// governs lan_stream_crio (D4). The ace_full reserve 600..10000 is DEFERRED
/// (nobody writes) ⇒ NO mirror row — external FC06/FC16 writes into the
/// reserve persist forever. Copy ranges are CLAMPED to the runtime register
/// file: a smaller bus (e.g. a legal 650-reg file) simply skips the absent
/// tail. A new zone later = one row in the mirror table — additive, zero
/// publisher logic changes (rule_n_plus_1).
/// </summary>
public sealed class ModbusTcpPublisher : IModbusPublisher, IDisposable
{
    /// <summary>FC03/FC04 maximum read quantity (Modbus application spec).</summary>
    private const int MaxReadQuantity = 125;

    /// <summary>FC16 maximum write quantity (Modbus application spec).</summary>
    private const int MaxWriteQuantity = 123;

    private readonly ILatestBusState _latest;
    private readonly ModbusOptions _options;
    private readonly TimeSpan _tickInterval;
    private readonly object _regLock = new();

    /// <summary>
    /// The register file. RUNTIME-SIZED: initially options.Total
    /// (ini total — the configured dictionary), re-shaped to the bus header's
    /// self-describing regCount on every fresh snapshot. ALL bounds checks
    /// (FC03/FC04/FC06/FC16) and the tick copy use THIS array's length — the
    /// register map edge moves with the runtime bus, never a hardcoded const.
    /// </summary>
    private ushort[] _registers;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Generation? _generation;
    private bool _disposed;

    private sealed class Generation(TcpListener listener, CancellationTokenSource cancellation)
    {
        internal readonly TcpListener Listener = listener;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal int Bound = 1;
        internal Task Accept = Task.CompletedTask;
        internal Task Refresh = Task.CompletedTask;
        internal readonly HashSet<Task> Clients = new();
    }

    /// <summary>
    /// Current register file size in registers (dynamic). = ini Total before the
    /// first fresh snapshot, = the bus regCount afterwards. Internal — contract
    /// tests assert the runtime sizing law against this value.
    /// </summary>
    internal int RegisterCount { get { lock (_regLock) return _registers.Length; } }

    /// <summary>
    /// Executed tick count since construction. Internal — loop-rate observability
    /// for contract tests (InternalsVisibleTo); incremented once per TickOnce on
    /// the refresh thread.
    /// </summary>
    internal long TickCount;

    /// <summary>
    /// Last bus error observed by the tick loop (BusReader.LastError at the
    /// moment a tick obtained NO snapshot), null while the last snapshot was
    /// healthy. OBSERVABILITY (INC-11): the publisher degrades to header-zeros
    /// / stale-hold WITHOUT throwing by doctrine — a STUCK open failure (bus
    /// file shape rejected by the reader's shape gate, file absent) is
    /// otherwise invisible: the stand served all-zeros on :502 for hours
    /// against a provably healthy bus because a stale pre-migration build
    /// rejected the v5 shape every tick and nobody surfaced the error. This
    /// surface is the greppable trace; it self-clears on the first healthy
    /// snapshot (in-place recovery, no restart).
    /// </summary>
    internal string? LastBusError { get; private set; }

    /// <summary>
    /// Count of tick exceptions swallowed by the refresh loop (the loop is
    /// kept alive per the stale-hold doctrine — but never silently, INC-11).
    /// </summary>
    internal long TickExceptionCount;

    /// <summary>Message of the most recent swallowed tick exception (null when none).</summary>
    internal string? LastTickError { get; private set; }

    /// <summary>
    /// Creates the publisher over a shared latest-state surface; acquisition
    /// and reader lifetime belong to the host.
    /// </summary>
    /// <param name="latest">Shared immutable acquisition publication.</param>
    /// <param name="options">TCP port (default 502) + configured dictionary Total (ini total)
    /// + tick cadence TickMs (ini modbus_tick_ms, default 500 ms; &lt; 1 ⇒ throw).</param>
    /// <param name="tickInterval">Explicit refresh-period override (tests only). When
    /// absent, the interval comes from options.TickMs — EXTERNAL config wins in
    /// production.</param>
    public ModbusTcpPublisher(ILatestBusState latest, ModbusOptions options, TimeSpan? tickInterval = null)
    {
        _latest = latest ?? throw new ArgumentNullException(nameof(latest));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.Total is < BusLayout.HeaderRegCount or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), _options.Total,
                $"ModbusOptions.Total must be in [{BusLayout.HeaderRegCount}..{ushort.MaxValue}] " +
                $"(header regs must fit; the bus contract allows 650 ≤ regCount ≤ 65535)");
        if (_options.TickMs < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.TickMs,
                "ModbusOptions.TickMs must be >= 1 (station.ini modbus_tick_ms; " +
                "operator PLC range [200..500], default 500)");
        _registers = new ushort[_options.Total];
        _tickInterval = tickInterval is { } t && t > TimeSpan.Zero
            ? t
            : TimeSpan.FromMilliseconds(_options.TickMs);
    }

    // ── IModbusPublisher ────────────────────────────────────────────────────

    /// <summary>W10.1: only a successfully bound generation is published.</summary>
    public bool IsBound => Volatile.Read(ref _generation) is { } generation && Volatile.Read(ref generation.Bound) == 1;

    internal int BoundPort => ((IPEndPoint)_generation!.Listener.LocalEndpoint).Port;
    internal int ActiveClientCount
    {
        get
        {
            var generation = Volatile.Read(ref _generation);
            if (generation is null) return 0;
            lock (generation.Clients) return generation.Clients.Count;
        }
    }

    /// <summary>
    /// The register map is RUNTIME-SIZED from the bus header (regCount
    /// self-describing) — no register-map configuration exists. Retained ONLY as
    /// an inert v1 no-op for the legacy pipeline contract (see IModbusPublisher);
    /// the publisher mirrors the bus snapshot every tick.
    /// </summary>
    public Task ConfigureAsync(IReadOnlyList<ModbusRegisterEntry> registerMap) => Task.CompletedTask;

    /// <summary>Starts one generation. Repeated start while bound is idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_generation is not null) return;

            var port = _options.Port;
            var listener = new TcpListener(IPAddress.Any, port);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                try { listener.Start(); }
                catch (SocketException ex) { throw new ModbusBindException(port, ex); }
                var generation = new Generation(listener, cancellation);
                generation.Accept = AcceptLoopAsync(generation);
                generation.Refresh = RefreshLoopAsync(cancellation.Token);
                Volatile.Write(ref _generation, generation);
            }
            catch
            {
                listener.Dispose();
                cancellation.Dispose();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopGenerationAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopGenerationAsync()
    {
        var generation = _generation;
        if (generation is null) return;
        try
        {
            generation.Cancellation.Cancel();
            Volatile.Write(ref generation.Bound, 0);
            generation.Listener.Stop();
            // Accept must finish before the final client snapshot: no late client escapes join.
            await generation.Accept.ConfigureAwait(false);
            Task[] clients;
            lock (generation.Clients) clients = generation.Clients.ToArray();
            await Task.WhenAll(clients.Append(generation.Refresh)).ConfigureAwait(false);
        }
        finally
        {
            generation.Listener.Dispose();
            generation.Cancellation.Dispose();
            Volatile.Write(ref _generation, null);
        }
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    private async Task DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopGenerationAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    // ── tick-loop bus→register-file copy (A4/A5; interval = options.TickMs) ──

    /// <summary>
    /// v5 mirror table — one row per bus zone; bounds from the BusLayout v5
    /// zone consts (bus register map v5): com_stream 4..199, lan_stream_crio
    /// 200..399, raw_com_line 400..599. ALL zones are AddIn-owned in v5; the
    /// ace_full reserve 600..10000 is DEFERRED (nobody writes) ⇒ NO row here.
    /// A new zone later = one row — additive, zero publisher logic changes
    /// (rule_n_plus_1). Zone bounds are data, ¬ publisher logic.
    /// </summary>
    private readonly record struct MirrorZone(ZoneKind Kind, int BaseReg, int RegCount);

    private static readonly MirrorZone[] Zones =
    {
        new(ZoneKind.ComMain, BusLayout.ComStreamBaseReg, BusLayout.ComStreamRegCount),
        new(ZoneKind.Lan, BusLayout.LanStreamCrioBaseReg, BusLayout.LanStreamCrioRegCount),
        new(ZoneKind.RawReserve, BusLayout.RawComLineBaseReg, BusLayout.RawComLineRegCount),
    };

    /// <summary>
    /// One refresh tick. Internal so the contract tests drive ticks
    /// deterministically (InternalsVisibleTo iCemDataMonitor.Transport.Tests); the
    /// production loop calls this every tick interval (options.TickMs). Plain
    /// overwrite every cycle — Δ-detection BANNED by operator directive.
    /// </summary>
    internal void TickOnce()
    {
        Interlocked.Increment(ref TickCount);
        var state = _latest.Current;
        var snap = state.Snapshot;
        // INC-11 observability: a NULL snapshot = bus-absent for this tick —
        // surface WHY (file absent / shape rejected / open failure). The
        // degradation itself is doctrine (header-zeros + hold); the SILENCE
        // was the defect. Clears on the first healthy snapshot.
        LastBusError = snap is null ? state.LastError : null;
        lock (_regLock)
        {
            if (snap is null)
            {
                // No snapshot ever taken (bus file never present): header = zeros,
                // no zone overwrite — registers hold (external writes live).
                Array.Clear(_registers, 0, BusLayout.HeaderRegCount);
                return;
            }

            // Runtime sizing law: the bus header is self-describing
            // (regCount @8..9) — the register file follows the RUNTIME bus size.
            // Stale snapshots are last-good copies whose regCount was already
            // applied ⇒ the no-op check keeps stale-hold stable (AC3/AC4).
            ResizeToBus(snap);

            // Header mirror — ALWAYS: words 0..HeaderRegCount (=4, layout v5
            // state block 0..3; last-good words when the snapshot is stale).
            snap.CopyWordsTo(0, _registers.AsSpan(0, BusLayout.HeaderRegCount));

            if (snap.Stale)
                return; // stale snapshot ⇒ no zone overwrite (stale-hold, AC3/AC4)

            foreach (var zone in Zones)
                CopyZoneIfActive(snap, zone);
        }
    }

    /// <summary>
    /// Re-shape the register file to the bus regCount (grow = zero-fill,
    /// shrink = truncate). No-op when the size already matches or the header
    /// value is not a usable dictionary size (defensive: the reader validates
    /// shapes on open, this guard only refuses to shrink below the header regs).
    /// </summary>
    private void ResizeToBus(BusSnapshot snap)
    {
        int target = snap.RegCount;
        if (target < BusLayout.HeaderRegCount || target == _registers.Length)
            return;

        var next = new ushort[target];
        Array.Copy(_registers, 0, next, 0, Math.Min(_registers.Length, target));
        _registers = next;
    }

    private void CopyZoneIfActive(BusSnapshot snap, MirrorZone zone)
    {
        if (!IsZoneActive(snap, zone.Kind))
            return; // A5: inactive zone untouched — external writes live

        // CLAMPED to the runtime arrays: a legal smaller bus (650 ≤ regCount)
        // simply skips the absent tail of the zone (e.g. lan_stream_crio
        // 200..399 on a 650-reg file ⇒ copy 0 words, zone holds).
        int len = Math.Min(zone.RegCount, snap.Words.Count - zone.BaseReg);
        len = Math.Min(len, _registers.Length - zone.BaseReg);
        if (len <= 0)
            return;

        snap.CopyWordsTo(zone.BaseReg, _registers.AsSpan(zone.BaseReg, len));
    }

    /// <summary>
    /// Zone liveness from the snapshot. v5 blocks: ComMain ⇒ com_stream flag,
    /// Lan ⇒ lan_stream_crio flag (D4 — the former Lan liveness block governs
    /// lan_stream_crio, writer = AddIn), RawReserve ⇒ raw_com_line flag. An
    /// unknown zone kind (snapshot schema lag — the reader's BusSnapshot
    /// predates this layout version) degrades to INACTIVE: no positive
    /// liveness signal ⇒ HOLD, never overwrite external writes (A5 fail-safe
    /// direction — same philosophy as stale-hold AC3/AC4). One zone's seam gap
    /// must not kill mirroring of the other zones.
    /// </summary>
    private static bool IsZoneActive(BusSnapshot snap, ZoneKind kind)
    {
        try
        {
            return snap.ZoneInfo(kind).Active;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    TickOnce();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Bus hiccup is non-fatal — keep the loop alive (stale-hold),
                    // but NEVER silently (INC-11): record the swallowed failure.
                    Interlocked.Increment(ref TickExceptionCount);
                    LastTickError = ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    // ── TCP accept loop ─────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(Generation generation)
    {
        var ct = generation.Cancellation.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await generation.Listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; }

            var task = HandleClientAsync(client, ct);
            lock (generation.Clients) generation.Clients.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (generation.Clients) generation.Clients.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // ── Per-connection handler (FC03/FC04 read, FC06/FC16 write) ────────────

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    // 6-byte MBAP header
                    var hdr = await ReadExactAsync(stream, 6, ct).ConfigureAwait(false);
                    if (hdr == null) break;

                    ushort tid = (ushort)((hdr[0] << 8) | hdr[1]);
                    ushort pid = (ushort)((hdr[2] << 8) | hdr[3]);
                    ushort length = (ushort)((hdr[4] << 8) | hdr[5]);

                    // PDU body (unitId + func + data)
                    var body = await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
                    if (body == null) break;

                    byte[] rspBody = body.Length >= 2
                        ? ProcessRequest(body[0], body[1], body)
                        : ExceptionBody(0, 0x80, 0x01); // malformed ⇒ illegal function

                    // MBAP response header
                    ushort rspLength = (ushort)rspBody.Length;
                    var mbap = new byte[6];
                    mbap[0] = (byte)((tid >> 8) & 0xFF);
                    mbap[1] = (byte)(tid & 0xFF);
                    mbap[2] = (byte)((pid >> 8) & 0xFF);
                    mbap[3] = (byte)(pid & 0xFF);
                    mbap[4] = (byte)((rspLength >> 8) & 0xFF);
                    mbap[5] = (byte)(rspLength & 0xFF);

                    await stream.WriteAsync(mbap, ct).ConfigureAwait(false);
                    await stream.WriteAsync(rspBody, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch { /* per-connection errors are isolated */ }
    }

    private byte[] ProcessRequest(byte unitId, byte funcCode, byte[] body) => funcCode switch
    {
        0x03 or 0x04 => ProcessRead(unitId, funcCode, body),
        0x06 => ProcessWriteSingle(unitId, body),
        0x10 => ProcessWriteMultiple(unitId, body),
        _ => ExceptionBody(unitId, funcCode, 0x01), // illegal function
    };

    private byte[] ProcessRead(byte unitId, byte funcCode, byte[] body)
    {
        if (body.Length < 6)
            return ExceptionBody(unitId, funcCode, 0x03); // malformed PDU

        ushort startAddr = BigEndian(body, 2);
        ushort qty = BigEndian(body, 4);

        if (qty == 0 || qty > MaxReadQuantity)
            return ExceptionBody(unitId, funcCode, 0x03); // illegal data value

        var data = new byte[qty * 2];
        lock (_regLock)
        {
            // Bounds against the DYNAMIC register file (runtime bus size), so a
            // re-shaped file moves the legal edge with the bus.
            if ((int)startAddr + qty > _registers.Length)
                return ExceptionBody(unitId, funcCode, 0x02); // illegal data address

            for (int i = 0; i < qty; i++)
            {
                ushort val = _registers[startAddr + i];
                data[i * 2] = (byte)((val >> 8) & 0xFF);
                data[i * 2 + 1] = (byte)(val & 0xFF);
            }
        }

        var rsp = new byte[3 + data.Length];
        rsp[0] = unitId;
        rsp[1] = funcCode;
        rsp[2] = (byte)data.Length;
        Array.Copy(data, 0, rsp, 3, data.Length);
        return rsp;
    }

    private byte[] ProcessWriteSingle(byte unitId, byte[] body)
    {
        if (body.Length < 5)
            return ExceptionBody(unitId, 0x06, 0x03); // malformed PDU

        ushort addr = BigEndian(body, 2);
        ushort value = BigEndian(body, 4); // body[2..3] = addr, body[4..5] = value

        lock (_regLock)
        {
            if (addr >= _registers.Length)
                return ExceptionBody(unitId, 0x06, 0x02); // illegal data address
            _registers[addr] = value;
        }

        // Echo: unitId + func + addr + value
        return new byte[] { unitId, 0x06, body[2], body[3], body[4], body[5] };
    }

    private byte[] ProcessWriteMultiple(byte unitId, byte[] body)
    {
        if (body.Length < 8)
            return ExceptionBody(unitId, 0x10, 0x03); // malformed PDU

        ushort addr = BigEndian(body, 2);
        ushort qty = BigEndian(body, 4);
        byte byteCount = body[6];

        if (qty == 0 || qty > MaxWriteQuantity || byteCount != qty * 2 || body.Length < 7 + byteCount)
            return ExceptionBody(unitId, 0x10, 0x03); // illegal data value

        lock (_regLock)
        {
            // Bounds against the DYNAMIC register file (runtime bus size).
            if ((int)addr + qty > _registers.Length)
                return ExceptionBody(unitId, 0x10, 0x02); // illegal data address

            for (int i = 0; i < qty; i++)
                _registers[addr + i] = BigEndian(body, 7 + i * 2);
        }

        // Echo: unitId + func + addr + qty
        return new byte[] { unitId, 0x10, body[2], body[3], body[4], body[5] };
    }

    private static byte[] ExceptionBody(byte unitId, byte funcCode, byte code) =>
        new byte[] { unitId, (byte)(funcCode | 0x80), code };

    private static ushort BigEndian(byte[] buf, int offset) =>
        (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0) return null; // connection closed
            offset += read;
        }
        return buf;
    }
}
