// IEmuControl — control-plane abstraction for the operator command channel
// (named pipe \\.\pipe\iCemEmule.Control + `iCemEmule.exe --ctl` CLI).
// Implementations MUST be callable from pipe-server worker threads; the
// production adapter (Ui/EmuControlAdapter) marshals every call onto the UI
// thread, so providers/engines keep their existing single-threaded affinity.

namespace Halliburton.iCemEmule.Control;

public interface IEmuControl
{
    /// <summary>Full machine-readable state snapshot (status/watch payload).</summary>
    Dictionary<string, object?> GetStatus();

    /// <summary>Start streaming on channel "com"|"lan". Idempotent.</summary>
    ControlResult StartChannel(string channel);

    /// <summary>Stop streaming on channel "com"|"lan" and release port/adapter. Idempotent.</summary>
    ControlResult StopChannel(string channel);

    /// <summary>Override an emulated value at runtime, in the variable's NATIVE units.
    /// channel null ⇒ resolve on both channels; sv null ⇒ resolve by signal name.</summary>
    ControlResult SetVariable(string? channel, string name, double value, uint? sv);

    /// <summary>Remove a runtime override (idempotent; cleared list may be empty).</summary>
    ControlResult ClearVariable(string? channel, string name, uint? sv);

    /// <summary>Current emulated value (native units; LAN = decoded from the pre-conversion superframe).</summary>
    ControlResult GetVariable(string? channel, string name, uint? sv);

    /// <summary>COM device templates = switchable profiles.</summary>
    ControlResult GetProfiles();

    /// <summary>Switch the COM template (profile). deviceType null ⇒ unique-name match.</summary>
    ControlResult SetProfile(string? deviceType, string name);

    /// <summary>List emulated variables per channel (null ⇒ both).</summary>
    ControlResult GetVariables(string? channel);

    // ── W11.7: full functional surface (embedded UI parity with the old tabs) ──

    /// <summary>Enumerate available serial ports (COM).</summary>
    ControlResult GetPorts();

    /// <summary>Enumerate available LAN capture adapters.</summary>
    ControlResult GetAdapters();

    /// <summary>Set + persist the COM port; restarts the stream if it was running.</summary>
    ControlResult SetPort(string port);

    /// <summary>Set + persist the LAN adapter; restarts the stream if it was running.</summary>
    ControlResult SetAdapter(string adapter);

    /// <summary>Set + persist the ACE equipment number.</summary>
    ControlResult SetEquipment(uint equipment);

    /// <summary>Set + persist the firmware-SV flag (extra tables in the superframe).</summary>
    ControlResult SetFirmware(bool on);

    /// <summary>W14: emulation (auto-generation) on/off per channel. OFF = freeze the
    /// generated waveform; only manual `set` writes move values on the wire (flecsEmu
    /// "Emule" checkbox — the old bug where generated data kept flowing is fixed here).</summary>
    ControlResult SetEmulation(string channel, bool on);

    /// <summary>Set + persist the units preset (0..3, UnitPreset).</summary>
    ControlResult SetPreset(int presetIndex);

    /// <summary>Set + persist a per-group unit override (group: flow|temperature|density|
    /// volume|level|pressure; empty unit = clear the override, follow the preset).</summary>
    ControlResult SetUnit(string group, string? unit);

    /// <summary>Clean shutdown request (ACK is sent before the process exits).</summary>
    void RequestQuit();
}

public sealed class ControlResult
{
    public bool Ok { get; }
    public string? Error { get; }
    public Dictionary<string, object?>? Data { get; }

    private ControlResult(bool ok, string? error, Dictionary<string, object?>? data)
    {
        Ok = ok; Error = error; Data = data;
    }

    public static ControlResult Success(Dictionary<string, object?>? data = null) => new(true, null, data);
    public static ControlResult Fail(string error) => new(false, error, null);
}

/// <summary>One processed command: the JSON reply line plus server-side flags.</summary>
public sealed record ControlReply(string Cmd, string Json, bool StartWatch, bool Quit);
