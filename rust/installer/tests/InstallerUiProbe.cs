// Disposable Windows CI fixture. Never included in the payload or MSI.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

internal static class InstallerUiProbe
{
    private static Process client;
    private static StreamWriter log;

    private static string Name(AutomationElement element)
    {
        return element.Current.Name.Replace("&", "").Trim();
    }

    private static AutomationElement Find(ControlType type, string prefix)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, client.Id));
        foreach (AutomationElement window in windows)
        {
            var controls = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            foreach (AutomationElement control in controls)
            {
                if (control.Current.IsEnabled && Name(control).StartsWith(prefix, StringComparison.Ordinal))
                    return control;
            }
        }
        return null;
    }

    private static AutomationElement Wait(ControlType type, params string[] prefixes)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 15)
        {
            foreach (string prefix in prefixes)
            {
                var element = Find(type, prefix);
                if (element != null) return element;
            }
            if (client.HasExited) throw new InvalidOperationException("MSI UI exited before expected control");
            Thread.Sleep(100);
        }
        throw new TimeoutException("MSI UI control: " + String.Join("/", prefixes));
    }

    private static void Click(params string[] names)
    {
        var button = Wait(ControlType.Button, names);
        log.WriteLine("Click " + Name(button));
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static TogglePattern Check(string name, ToggleState expected)
    {
        var checkbox = Wait(ControlType.CheckBox, name);
        var pattern = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
        log.WriteLine(Name(checkbox) + " = " + pattern.Current.ToggleState);
        if (pattern.Current.ToggleState != expected)
            throw new InvalidOperationException("Checkbox state differs: " + name + ", expected " + expected);
        return pattern;
    }

    private static void Options(ToggleState expected)
    {
        Check("Создать ярлык", expected);
        Check("Добавить Host", expected);
        Check("Принудительно закрыть", ToggleState.Off);
    }

    private static void WaitForExit(int expectedExit)
    {
        var clock = Stopwatch.StartNew();
        bool finished = false;
        while (!client.WaitForExit(100))
        {
            if (clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("MSI UI operation exceeded 180 seconds; no retry");
            var finish = Find(ControlType.Button, "Finish");
            if (!finished && finish != null)
            {
                ((InvokePattern)finish.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                finished = true;
            }
        }
        if (client.ExitCode != expectedExit)
            throw new InvalidOperationException("MSI UI exit " + client.ExitCode + ", expected " + expectedExit);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // MSI, log path, saved option state, explicit CLI zero, apply UI opt-out.
        if (args.Length != 5) return 2;
        using (log = new StreamWriter(args[1] + ".ui.log"))
        {
            log.AutoFlush = true;
            try
            {
                bool apply = args[4] == "1";
                var expected = args[2] == "1" ? ToggleState.On : ToggleState.Off;
                string properties = args[3] == "1" ? " CREATEDESKTOPSHORTCUT=0 ADDAUTOSTART=0 MSIRMSHUTDOWN=0" : "";
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/i \"" + args[0] + "\" /norestart /l*v \"" + args[1] + "\"" + properties);
                start.UseShellExecute = false;
                client = Process.Start(start);
                Click("Next");
                Click("Repair");
                Options(expected);
                Check("Принудительно закрыть", ToggleState.Off).Toggle();
                Check("Принудительно закрыть", ToggleState.On).Toggle();
                Check("Принудительно закрыть", ToggleState.Off);
                if (apply)
                {
                    if (expected != ToggleState.On) throw new InvalidOperationException("Apply probe requires saved opt-in");
                    Check("Создать ярлык", ToggleState.On).Toggle();
                    Check("Добавить Host", ToggleState.On).Toggle();
                    expected = ToggleState.Off;
                }
                Click("Далее");
                Click("Back");
                Options(expected);
                if (apply)
                {
                    Click("Далее");
                    Click("Repair");
                    WaitForExit(0);
                }
                else
                {
                    Click("Отмена");
                    Click("Yes");
                    WaitForExit(1602);
                }
                log.WriteLine("PASS MSI UI: default force Off; saved/CLI options; toggle and Back; " + (apply ? "committed opt-out repair" : "Cancel without mutation"));
                return 0;
            }
            catch (Exception error)
            {
                log.WriteLine(error);
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement window in windows)
                    log.WriteLine("Window PID=" + window.Current.ProcessId + " " + Name(window));
                return 1;
            }
            finally
            {
                if (client != null)
                {
                    if (!client.HasExited) { client.Kill(); client.WaitForExit(5000); }
                    client.Dispose();
                }
            }
        }
    }
}
