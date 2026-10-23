// UiTrace — W15 diagnostic (2026-08-28, owner's «смотри на логи» while
// testing the switches): a never-throw file trace of the UI command chain —
// click → Toggled → VM command → pipe/in-proc reply → note. Written to
// %TEMP%\emu_ui_trace.log (append; survives restarts). Every call is guarded:
// tracing must NEVER take the emulator down. The toggles died silently
// (ReactiveCommand swallows unobserved ThrownExceptions) — this is the
// instrument that ends the guessing.

namespace iCemDataMonitor.Emulator.WinUI;

/// <summary>Public: the app project (MainWindow pointer probes) also traces.</summary>
public static class UiTrace
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_ui_trace.log");

    public static void Log(string message)
    {
        try
        {
            iCemDataMonitor.Sdk.Diagnostics.StationFileLog.WriteFormatted(Path,
                    $"[{DateTime.Now:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId}] {message}\r\n");
        }
        catch
        {
            // never throw from tracing
        }
    }
}
