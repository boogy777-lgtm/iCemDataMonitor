// ControlPipeServer — named-pipe JSON-lines server for the operator command
// channel (\\.\pipe\iCemEmule.Control). One JSON command line in → one JSON
// reply line out; `watch` keeps the connection open and streams a status
// object every 1 s until the client disconnects. Dependency-free
// (System.IO.Pipes, BCL). Every executor call is guarded: a faulty command
// can NEVER crash the server or the emitter loops.

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Halliburton.iCemEmule.Control;

public sealed class ControlPipeServer : IDisposable
{
    public const string DefaultPipeName = "iCemEmule.Control";

    private readonly string _pipeName;
    private readonly Func<string, ControlReply> _execute;
    private readonly Func<string> _watchTick;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _active = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private Task? _acceptLoop;

    internal CancellationToken Stopping => _cts.Token;
    internal int ActiveHandlerCount => _handlers.Count;

    /// <param name="execute">one command line → reply (must be concurrency-safe).</param>
    /// <param name="watchTick">produces the 1 Hz watch payload (JSON status line).</param>
    public ControlPipeServer(Func<string, ControlReply> execute, Func<string> watchTick,
        string pipeName = DefaultPipeName)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _watchTick = watchTick ?? throw new ArgumentNullException(nameof(watchTick));
        _pipeName = pipeName;
    }

    public void Start()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _active[pipe] = 0;
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var handed = pipe;
                pipe = null; // ownership moves to the client task
                // Execution may request quit immediately. Publish its drain task
                // before allowing user code to run; Task.Run-before-register races
                // Dispose's snapshot and can close a pipe while its ACK is in flight.
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var handler = HandleRegisteredClientAsync(handed, registered.Task);
                _handlers[handler] = 0;
                _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                registered.SetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // Accept failure must not kill the loop — brief backoff and retry.
                try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (pipe is not null)
                {
                    _active.TryRemove(pipe, out _);
                    try { pipe.Dispose(); } catch { }
                }
            }
        }
    }

    private async Task HandleRegisteredClientAsync(NamedPipeServerStream pipe, Task registered)
    {
        await registered.ConfigureAwait(false);
        await HandleClientAsync(pipe).ConfigureAwait(false);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch { break; }
                    if (line is null) break;

                    ControlReply reply;
                    try { reply = _execute(line); }
                    catch (Exception ex)
                    {
                        reply = new ControlReply("",
                            CommandProcessor.ToJson(false, "", null, $"internal error: {ex.Message}"),
                            false, false);
                    }

                    try { await writer.WriteLineAsync(reply.Json).ConfigureAwait(false); }
                    catch { break; }

                    if (reply.StartWatch)
                    {
                        while (pipe.IsConnected && !_cts.IsCancellationRequested)
                        {
                            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                            string tick;
                            try { tick = _watchTick(); }
                            catch (Exception ex)
                            {
                                tick = CommandProcessor.ToJson(false, "watch", null, $"internal error: {ex.Message}");
                            }
                            try { await writer.WriteLineAsync(tick).ConfigureAwait(false); }
                            catch { break; } // client gone
                        }
                        break; // watch owns the connection until disconnect
                    }

                    if (reply.Quit) break;
                }
            }
        }
        catch { /* client aborted mid-command — best effort */ }
        finally
        {
            _active.TryRemove(pipe, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Quit-ACK drain (race fix 2026-08-26): the engine main thread calls
        // Dispose right after RequestQuit() sets the quit event — while the
        // pipe worker may still be writing the `quit` ACK line. Tearing the
        // pipes down before that write completes loses the ACK and the
        // operator client sees "connection closed by server" (contract:
        // "ACK is sent before the process exits", IEmuControl.RequestQuit).
        // Per-command clients finish immediately; watch loops observe the
        // cancellation within ≤1 tick; a truly idle reader bounds the wait.
        try { Task.WhenAll(_handlers.Keys).Wait(TimeSpan.FromSeconds(1.5)); } catch { }
        foreach (var p in _active.Keys)
        {
            try { p.Dispose(); } catch { }
        }
        try { _acceptLoop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
