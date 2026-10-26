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

    private static AutomationElement Find(ControlType type, string prefix, bool enabledOnly = true)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, client.Id));
        foreach (AutomationElement window in windows)
        {
            AutomationElementCollection controls;
            try
            {
                controls = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            }
            catch (ElementNotAvailableException)
            {
                // A wizard transition can retire a window from this observation snapshot.
                continue;
            }
            foreach (AutomationElement control in controls)
            {
                try
                {
                    if ((!enabledOnly || control.Current.IsEnabled) && Name(control).StartsWith(prefix, StringComparison.Ordinal))
                        return control;
                }
                catch (ElementNotAvailableException)
                {
                    // Keep discovery read-only; the caller polls again within its existing budget.
                    continue;
                }
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

    private static AutomationElement ExactControl(AutomationElement window, ControlType type, string name)
    {
        foreach (AutomationElement control in window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
        {
            if (control.Current.IsEnabled && Name(control) == name) return control;
        }
        return null;
    }

    private static bool ConfirmRestartManager()
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, client.Id));
        foreach (AutomationElement window in windows)
        {
            try
            {
                // Stock WiX MsiRMFilesInUse requires separate consent in full UI.
                // Identify both alternatives in this window before selecting closure;
                // never accept an arbitrary OK, Ignore, Retry or reboot prompt.
                var close = ExactControl(window, ControlType.RadioButton,
                    "Close the applications and attempt to restart them.");
                var reboot = ExactControl(window, ControlType.RadioButton,
                    "Do not close applications. A reboot will be required.");
                var ok = ExactControl(window, ControlType.Button, "OK");
                if (close == null || reboot == null || ok == null) continue;
                var selection = (SelectionItemPattern)close.GetCurrentPattern(SelectionItemPattern.Pattern);
                selection.Select();
                if (!selection.Current.IsSelected)
                    throw new InvalidOperationException("Restart Manager close option was not selected");
                log.WriteLine("MsiRMFilesInUse: selected " + Name(close) + "; click " + Name(ok));
                ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                return true;
            }
            catch (ElementNotAvailableException)
            {
                // A transition retired this read snapshot; poll again within the deadline.
                continue;
            }
        }
        return false;
    }

    private static void DumpWindows()
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
        {
            try
            {
                log.WriteLine("Window PID=" + window.Current.ProcessId + " " + Name(window));
                if (client == null || window.Current.ProcessId != client.Id) continue;
                foreach (AutomationElement control in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try
                    {
                        log.WriteLine("  " + control.Current.ControlType.ProgrammaticName +
                            " id=" + control.Current.AutomationId + " enabled=" + control.Current.IsEnabled +
                            " offscreen=" + control.Current.IsOffscreen + " name=" + Name(control));
                    }
                    catch (ElementNotAvailableException) { log.WriteLine("  Control retired during diagnostics"); }
                }
            }
            catch (ElementNotAvailableException) { log.WriteLine("Window retired during diagnostics"); }
        }
    }

    private static void WaitForExit(int expectedExit, bool committed = false)
    {
        var clock = Stopwatch.StartNew();
        bool finished = false;
        bool confirmedRestartManager = false;
        while (!client.WaitForExit(100))
        {
            if (clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("MSI UI operation exceeded 180 seconds; no retry");
            if (committed && !confirmedRestartManager)
                confirmedRestartManager = ConfirmRestartManager();
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
        // MSI, log, saved options, explicit zero, opt-out, mode, AddIn path, runtime path.
        if (args.Length != 8) return 2;
        using (log = new StreamWriter(args[1] + ".ui.log"))
        {
            log.AutoFlush = true;
            try
            {
                bool apply = args[4] == "1";
                string mode = args[5];
                bool fresh = mode.StartsWith("fresh-", StringComparison.Ordinal);
                bool remove = mode == "remove";
                bool force = mode == "force-repair" || mode == "cancel-live";
                var expected = args[2] == "1" ? ToggleState.On : ToggleState.Off;
                string properties = args[3] == "1" ? " CREATEDESKTOPSHORTCUT=0 ADDAUTOSTART=0 MSIRMSHUTDOWN=0" : "";
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/i \"" + args[0] + "\" /norestart /l*v \"" + args[1] + "\"" + properties);
                if (fresh) start.Arguments += " MONITORROOT=\"" + args[7] + "\"";
                start.UseShellExecute = false;
                client = Process.Start(start);
                Click("Next");
                if (fresh)
                {
                    var directory = Wait(ControlType.Edit, "");
                    ((ValuePattern)directory.GetCurrentPattern(ValuePattern.Pattern)).SetValue(args[6]);
                    Click("Next");
                }
                else Click(remove ? "Remove" : "Repair");
                if (remove)
                {
                    Check("Принудительно закрыть", ToggleState.Off);
                    foreach (string label in new[] { "Создать ярлык", "Добавить Host" })
                    {
                        var option = Find(ControlType.CheckBox, label, false);
                        if (option == null || option.Current.IsEnabled)
                            throw new InvalidOperationException("Remove must disable option: " + label);
                    }
                }
                else Options(expected);
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
                if (force) Check("Принудительно закрыть", ToggleState.Off).Toggle();
                Click("Далее");
                Click("Back");
                Check("Принудительно закрыть", force ? ToggleState.On : ToggleState.Off);
                if (!remove)
                {
                    Check("Создать ярлык", expected);
                    Check("Добавить Host", expected);
                }
                if (fresh)
                {
                    Click("Назад");
                    var directory = Wait(ControlType.Edit, "");
                    string actual = ((ValuePattern)directory.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                    if (!String.Equals(actual.TrimEnd('\\'), args[6].TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Custom AddIn path lost on Back: " + actual);
                    Click("Next");
                    Options(expected);
                }
                if (apply || mode == "fresh-install" || remove || mode == "force-repair")
                {
                    Click("Далее");
                    Click(fresh ? "Install" : (remove ? "Remove" : "Repair"));
                    WaitForExit(0, true);
                }
                else
                {
                    Click("Отмена");
                    Click("Yes");
                    WaitForExit(1602);
                }
                log.WriteLine("PASS MSI UI mode=" + mode + "; options, force choice and navigation checked; exit verified");
                return 0;
            }
            catch (Exception error)
            {
                log.WriteLine(error);
                try { DumpWindows(); }
                catch (Exception diagnostics) { log.WriteLine("UI diagnostics failed: " + diagnostics); }
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
