// Windows Installer contract fixture only. Never shipped in a release.
using System;
using System.IO;
using System.Windows.Forms;

internal sealed class LockedFileOwner : Form
{
    private readonly FileStream held;
    private readonly string marker;
    private readonly bool stubborn;

    private LockedFileOwner(string[] args)
    {
        held = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        marker = args[1];
        stubborn = args[2] == "stubborn";
        Text = "iCem installer lock contract";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        File.WriteAllText(marker + ".events.log", "visible GUI window ready; stubborn=" + stubborn + Environment.NewLine);
        File.WriteAllText(marker, "ready");
    }

    protected override void WndProc(ref Message message)
    {
        // Refuse WM_CLOSE, WM_QUERYENDSESSION and WM_ENDSESSION. RM must need
        // explicit force consent to terminate this fixture; no natural expiry.
        if (message.Msg == 0x10 || message.Msg == 0x11 || message.Msg == 0x16)
        {
            File.AppendAllText(marker + ".events.log", message.Msg + ": stubborn=" + stubborn + Environment.NewLine);
            if (stubborn)
            {
                message.Result = IntPtr.Zero;
                return;
            }
            // WM_ENDSESSION can end the process without a FormClosed event.
            // Cooperatively release the actual resource before acknowledging;
            // this marker therefore proves graceful handling of the RM request.
            held.Dispose();
            File.WriteAllText(marker + ".closed", "graceful");
            message.Result = new IntPtr(1);
            Application.ExitThread();
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        held.Dispose();
        File.WriteAllText(marker + ".closed", "graceful");
        base.OnFormClosed(e);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.Run(new LockedFileOwner(args));
    }
}
