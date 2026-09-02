// TestEmitters — shared hardware-free emitter fakes for the engine-facing
// tests (introduced P5 2026-08-28 for InProcControlChannelTests; the older
// EngineStreamToggleTests keeps its private copies — production canon is NOT
// duplicated here, these are test doubles of the SAME interfaces).

using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;

namespace iCemEmule.Tests;

internal sealed class FakeComEmitter : IComEmitter
{
    public bool IsOpen { get; private set; }
    public string? PortName { get; private set; }
    public long LinesSent { get; private set; }
    public List<string> OpenCalls { get; } = new();
    public int CloseCalls { get; private set; }

    // interface contract; the engine under test never subscribes
    public event Action<string>? StatusChanged { add { } remove { } }

    public string Open(string portName)
    {
        OpenCalls.Add(portName);
        IsOpen = true;
        PortName = portName;
        return "";
    }

    public void SendLine(string line) => LinesSent++;
    public void Close() { CloseCalls++; IsOpen = false; PortName = null; }
    public void Dispose() => Close();
}

internal sealed class FakeLanEmitter : IAceLanEmitter
{
    public bool IsOpen { get; private set; }
    public string? AdapterName { get; private set; }
    public long FramesSent { get; private set; }
    public List<string> OpenCalls { get; } = new();
    public int CloseCalls { get; private set; }

    // interface contract; the engine under test never subscribes
    public event Action<string>? StatusChanged { add { } remove { } }

    public static IReadOnlyList<string> ListAdapters() => new[] { "fake0" };

    public string Open(string adapterDescriptionOrName)
    {
        OpenCalls.Add(adapterDescriptionOrName);
        IsOpen = true;
        AdapterName = adapterDescriptionOrName;
        return "";
    }

    public void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac)
        => FramesSent += vectors.Count;

    public void Close() { CloseCalls++; IsOpen = false; AdapterName = null; }
    public void Dispose() => Close();
}
