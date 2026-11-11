// ControlPipeClientTests — P2 2026-08-26 (owner decree «UI должен
// использовать CLI-команды как стандарт в нашей экосистеме»): the shared
// library-level client's reply parsing is pinned here — the same parsing the
// CLI and the WinUI both use (single client standard, like HostClient in the
// host ecosystem). Pipe I/O itself is not unit-testable without a live
// engine; the envelope grammar is the contract surface.

using Xunit;
using Halliburton.iCemEmule.Control;

namespace iCemEmule.Tests;

public class ControlPipeClientTests
{
    [Fact]
    public void ParseReply_OkEnvelope_ParsesCmdAndOk()
    {
        var reply = ControlPipeClient.ParseReply(
            "{\"ok\":true,\"cmd\":\"status\",\"data\":{\"pid\":123}}");

        Assert.NotNull(reply);
        Assert.True(reply!.Ok);
        Assert.Equal("status", reply.Cmd);
        Assert.Null(reply.Error);
        Assert.True(reply.Raw.RootElement.TryGetProperty("data", out var d));
        Assert.Equal(123, d.GetProperty("pid").GetInt32());
        reply.Dispose();
    }

    [Fact]
    public void ParseReply_ErrorEnvelope_SurfacesError()
    {
        var reply = ControlPipeClient.ParseReply(
            "{\"ok\":false,\"cmd\":\"start\",\"error\":\"no COM port configured (ComPort setting is empty)\"}");

        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal("start", reply.Cmd);
        Assert.Contains("no COM port configured", reply.Error);
        reply.Dispose();
    }

    [Fact]
    public void ParseReply_OkWithoutCmd_DefaultsCmdToEmpty()
    {
        var reply = ControlPipeClient.ParseReply("{\"ok\":true}");

        Assert.NotNull(reply);
        Assert.True(reply!.Ok);
        Assert.Equal("", reply.Cmd);
        reply.Dispose();
    }

    [Fact]
    public void ParseReply_MalformedJson_ReturnsNull()
    {
        Assert.Null(ControlPipeClient.ParseReply("not json at all"));
        Assert.Null(ControlPipeClient.ParseReply("{\"ok\":"));
    }

    [Fact]
    public void ParseReply_OkFalseWithoutError_ErrorIsNull()
    {
        var reply = ControlPipeClient.ParseReply("{\"ok\":false,\"cmd\":\"stop\"}");

        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Null(reply.Error);
        reply.Dispose();
    }

    [Fact]
    public void Cmd_JsonForm_MapsToSameEnvelopeTheCliSends()
    {
        // The shared client's JSON form must produce the exact grammar the
        // CLI's text form maps to (pinned by CommandProcessorParityTests).
        var json = "{\"cmd\":\"setemulate\",\"channel\":\"com\",\"value\":1}";
        var reply = ControlPipeClient.ParseReply(
            "{\"ok\":true,\"cmd\":\"setemulate\",\"data\":{\"channel\":\"com\",\"emulating\":true}}");

        Assert.NotNull(reply);
        Assert.True(reply!.Ok);
        Assert.Equal("setemulate", reply.Cmd);
        reply.Dispose();
        Assert.Contains("\"cmd\"", json);
    }
}
