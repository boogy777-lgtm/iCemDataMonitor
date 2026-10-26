// ControlPipeServerTests — xunit pins for the control-channel laws
// (Э4 iCemEmule: "control channel failure isolation" + quit-ACK contract).
// Every test uses a UNIQUE pipe name — the live engine owns
// \\.\pipe\iCemEmule.Control on the stand and must never be touched.

using System.IO.Pipes;
using System.Text;
using Halliburton.iCemEmule.Control;
using Xunit;

namespace iCemEmule.Tests;

public class ControlPipeServerTests
{
    private static string UniquePipe() => "iCemEmule.Tests." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Quit-ACK drain law (race fix 2026-08-26): the engine main thread calls
    /// Dispose right after RequestQuit() — previously it tore the pipes down
    /// while the worker was still writing the `quit` ACK, so the operator
    /// client saw "connection closed by server" instead of the ACK
    /// (contract: "ACK is sent before the process exits"). Dispose must drain
    /// in-flight replies before dismantling the pipes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DrainsInFlightReply_QuitAckReachesClient(bool textCommand)
    {
        using var release = new ManualResetEventSlim(false);
        using var pickedUp = new ManualResetEventSlim(false);
        using var stopping = new ManualResetEventSlim(false);
        var pipeName = UniquePipe();

        ControlPipeServer? server = null;
        server = new ControlPipeServer(
            line =>
            {
                Assert.Equal(textCommand ? "quit" : "{\"cmd\":\"quit\"}", line);
                Assert.True(server!.ActiveHandlerCount > 0, "command ran before drain registration");
                pickedUp.Set();
                Assert.True(release.Wait(5000), "test did not release the in-flight ACK");
                return new ControlReply("quit",
                    "{\"ok\":true,\"cmd\":\"quit\",\"data\":{\"quitting\":true}}", false, true);
            },
            () => "{}",
            pipeName);

        Task? disposeTask = null;
        using var registration = server.Stopping.Register(() => stopping.Set());
        try
        {
            server.Start();
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await client.ConnectAsync(5000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            await writer.WriteLineAsync(textCommand ? "quit" : "{\"cmd\":\"quit\"}");
            Assert.True(pickedUp.Wait(5000), "server never picked up the command");
            var readTask = reader.ReadLineAsync();

            // Dispose blocks synchronously, so give it a dedicated thread. A pool
            // worker plus Task.Delay(200) can starve the releasing continuation
            // beyond the real 1.5 s drain budget on loaded CI runners.
            disposeTask = Task.Factory.StartNew(server.Dispose, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(stopping.Wait(5000), "Dispose never started cancellation");
            Assert.False(disposeTask.IsCompleted, "Dispose abandoned the held reply");
            release.Set();

            var reply = (await readTask.WaitAsync(TimeSpan.FromSeconds(5)) ?? string.Empty).Trim();
            Assert.StartsWith("{", reply);
            Assert.Contains("\"ok\":true", reply);
            Assert.Contains("\"quitting\":true", reply);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            if (disposeTask is null) server.Dispose();
            else await disposeTask.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    [Fact]
    public async Task Dispose_WithIdleConnectedClient_HasFiniteDrainBudget()
    {
        var pipeName = UniquePipe();
        var server = new ControlPipeServer(_ => throw new InvalidOperationException("idle client sent a command"), () => "{}", pipeName);
        server.Start();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        Task? disposeTask = null;
        try
        {
            await client.ConnectAsync(5000);
            Assert.True(SpinWait.SpinUntil(() => server.ActiveHandlerCount > 0, 5000), "idle handler was not registered");
            disposeTask = Task.Factory.StartNew(server.Dispose, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.Dispose();
            if (disposeTask is null) server.Dispose();
            else await disposeTask.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>
    /// Failure-isolation law: malformed input ALWAYS yields {"ok":false} JSON,
    /// never throws, and the connection stays usable for the next command.
    /// </summary>
    [Fact]
    public async Task MalformedLines_YieldOkFalse_AndConnectionSurvives()
    {
        var pipeName = UniquePipe();
        var server = new ControlPipeServer(
            line => CommandProcessor.ProcessLine(line, new ThrowingControl()),
            () => "{}",
            pipeName);

        server.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);

        foreach (var bad in new[] { "{ not json", "unknowncmd", "" })
        {
            writer.WriteLine(bad);
            var line = await ReadLineWithTimeout(reader);
            Assert.False(string.IsNullOrWhiteSpace(line), $"no reply for '{bad}'");
            Assert.Contains("\"ok\":false", line);
        }

        // Connection survived the garbage — a well-formed command still round-trips.
        writer.WriteLine("help");
        var help = await ReadLineWithTimeout(reader);
        Assert.Contains("\"ok\":true", help);

        server.Dispose();
    }

    private static async Task<string> ReadLineWithTimeout(StreamReader reader)
    {
        var readTask = reader.ReadLineAsync();
        var finished = await Task.WhenAny(readTask, Task.Delay(5000));
        Assert.Same(readTask, finished);
        return await readTask ?? string.Empty;
    }

    /// <summary>Control plane that fails loudly — commands reaching it here are bugs.</summary>
    private sealed class ThrowingControl : IEmuControl
    {
        public Dictionary<string, object?> GetStatus() => throw new NotSupportedException();
        public ControlResult StartChannel(string channel) => throw new NotSupportedException();
        public ControlResult StopChannel(string channel) => throw new NotSupportedException();
        public ControlResult SetVariable(string? channel, string name, double value, uint? sv) => throw new NotSupportedException();
        public ControlResult ClearVariable(string? channel, string name, uint? sv) => throw new NotSupportedException();
        public ControlResult GetVariable(string? channel, string name, uint? sv) => throw new NotSupportedException();
        public ControlResult GetProfiles() => throw new NotSupportedException();
        public ControlResult SetProfile(string? deviceType, string name) => throw new NotSupportedException();
        public ControlResult GetVariables(string? channel) => throw new NotSupportedException();
        public ControlResult GetPorts() => throw new NotSupportedException();
        public ControlResult GetAdapters() => throw new NotSupportedException();
        public ControlResult SetPort(string port) => throw new NotSupportedException();
        public ControlResult SetAdapter(string adapter) => throw new NotSupportedException();
        public ControlResult SetEquipment(uint equipment) => throw new NotSupportedException();
        public ControlResult SetFirmware(bool on) => throw new NotSupportedException();
        public ControlResult SetEmulation(string channel, bool on) => throw new NotSupportedException();
        public ControlResult SetPreset(int presetIndex) => throw new NotSupportedException();
        public ControlResult SetUnit(string group, string? unit) => throw new NotSupportedException();
        public void RequestQuit() => throw new NotSupportedException();
    }
}
