// Program.cs — entry point of the MERGED emulator app (owner decree
// 2026-08-27: «Эмулятор теперь будет отдельным и независимым процессом...
// встроенный UI + CLI»). One process, three entry modes:
//   (no args)          UI mode: single-instance mutex + WinUI 3 window; the
//                      engine runs IN-PROCESS (EmuEngineHost hosted by App).
//   --cli <command>    one-shot console command against the RUNNING instance
//                      over \\.\pipe\iCemEmule.Control (same JSON grammar and
//                      exit-code discipline as the old --ctl; honest
//                      «эмулятор не запущен» when no instance runs).
//   --selftest         FrameRoundTrip.RunAll() ⇒ exit(failCount).
//   --landiag <a>      LAN loopback diagnostic.
// Mode dispatch is pinned by EmulatorAppArgs (iCemEmule.Core) —
// iCemEmule.Tests exercise it without WinUI.
//
// Custom Main replaces the XAML-generated main (DISABLE_XAML_GENERATED_MAIN):
// the CLI paths exit BEFORE any WinUI bootstrap; UI mode runs the standard
// WinUI bootstrap by hand.

using System.Runtime.InteropServices;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.SelfTest;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Halliburton.iCemEmulator;

public static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    public static int Main(string[] args)
    {
        var mode = EmulatorAppArgs.Parse(args, out var rest);

        switch (mode)
        {
            case EmulatorAppMode.Cli:
                // One-shot console command to the running instance. The mutex is
                // NOT taken here — this is the sanctioned second-process path.
                return ControlClient.Run(rest);

            case EmulatorAppMode.SelfTest:
                return FrameRoundTrip.RunAll();

            case EmulatorAppMode.LanDiag:
                if (rest.Length < 1)
                {
                    WriteConsole(EmulatorAppArgs.Usage + "\r\n--landiag requires an adapter substring\r\n");
                    return 2;
                }
                return LanLoopbackDiag.Run(rest[0]);

            case EmulatorAppMode.Help:
                WriteConsole(EmulatorAppArgs.Usage + "\r\n");
                return 0;
        }

        // ── UI mode: single-instance guard, then the WinUI window ─────────
        using var mutex = new Mutex(true, EmulatorAppContract.SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // An instance already runs: keep the console visible on a manual
            // double-click (WinExe has no console otherwise), then exit.
            WriteConsole("iCemEmulator is already running (mutex " +
                EmulatorAppContract.SingleInstanceMutexName + " held).\r\n" +
                "CLI: iCemEmulator.exe --cli status\r\n");
            if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
            {
                WriteConsole("Press any key to exit…\r\n");
                try { Console.ReadKey(intercept: true); } catch { }
            }
            return 1;
        }

        // Keep the mutex alive for the whole UI session (disposed at exit).
        GC.KeepAlive(mutex);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
            GC.KeepAlive(app);
        });
        return 0;
    }

    /// <summary>Best-effort console output: attach to the parent console when
    /// stdout is not redirected (interactive `iCemEmulator.exe --cli ...` from
    /// cmd/PowerShell), else write to the redirect stream.</summary>
    private static void WriteConsole(string text)
    {
        try
        {
            if (!Console.IsOutputRedirected)
                AttachConsole(AttachParentProcess);
            using var sw = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            sw.Write(text);
        }
        catch
        {
            // swallow — the message is best-effort by design
        }
    }
}
