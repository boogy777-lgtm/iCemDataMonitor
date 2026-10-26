using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReactiveUI;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.HostClient;

namespace iCemDataMonitor.Shell.WinUI;

/// <summary>
/// W21.4 boot splashscreen: a dumb MIRROR of the host's boot stages. The host
/// publishes typed BootStage records (replay + live over SubscribeBootStages);
/// each stage renders as a checkbox — completed ⇒ checked. The window closes
/// itself when the «ready» stage completes (or on timeout/failure — honest).
/// </summary>
public sealed partial class BootSplashWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>The stage whose completion means «host is up».</summary>
    public const string ReadyStageId = "ready";

    private readonly Dictionary<string, StageRow> _rows = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;

    public BootSplashWindow()
    {
        InitializeComponent();
        Title = "iCem Data Monitor — запуск";
        _dq = this.DispatcherQueue; // UI-thread queue captured once (CS0176: the
                                    // Window's DispatcherQueue PROPERTY shadows
                                    // the static GetForCurrentThread here)

        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "iCemDataMonitor.ico")); }
        catch { /* cosmetic */ }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int round = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch { /* cosmetic */ }
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 460, Height = 420 }); }
        catch { /* cosmetic */ }
    }

    /// <summary>Pre-stream phase: only «ожидание ответа хоста…» is visible.</summary>
    public void ShowWaiting() => Activate();

    /// <summary>Pipe answered but the stream failed — honest footer, no fake progress.</summary>
    public void ReportStreamFailure(string message)
    {
        FooterLine.Text = $"зеркало этапов недоступно: {message}";
    }

    /// <summary>
    /// Attach the boot-stage stream and wait until the ready stage completes.
    /// Events arrive on a gRPC thread — everything touching the UI marshals
    /// through the DispatcherQueue. Returns true when «ready» was observed.
    /// </summary>
    public async Task<bool> RunBootMirrorAsync(IBootStageStream stream, Action<string> log)
    {
        var dq = _dq;

        void OnStage(BootStage s)
        {
            dq.TryEnqueue(() =>
            {
                if (!_rows.TryGetValue(s.Id, out var row))
                {
                    row = BuildStageRow(s);
                    _rows[s.Id] = row;
                    StagesHost.Children.Add(row.Root);
                }
                row.Item.IsDone = s.Completed;
                row.Glyph.Glyph = row.Item.Glyph;
                row.Detail.Text = s.Detail ?? string.Empty;

                if (s.Id == ReadyStageId && s.Completed)
                {
                    log("[splash] ready stage completed");
                    _ready.TrySetResult(true);
                }
            });
        }

        stream.Stage += OnStage;
        stream.Failed += ex =>
        {
            dq.TryEnqueue(() => ReportStreamFailure(ex.Message));
            // Stream failure does NOT mean boot failure — the pipe already
            // answers. Wait out the timeout instead of failing the launch.
            log($"[splash] boot stream failed: {ex.Message}");
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var winner = await Task.WhenAny(_ready.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token))
            .ConfigureAwait(true);
        return winner == _ready.Task;
    }

    /// <summary>One checkbox row: item state + direct control references.</summary>
    private sealed record StageRow(BootStageItem Item, StackPanel Root, FontIcon Glyph, TextBlock Detail);

    /// <summary>
    /// P2 hardening (owner complaint #1): BuildStageRow used to index
    /// Application.Current.Resources directly — a missing theme key threw
    /// KeyNotFound on the FIRST stage row, the splash died mid-boot and the
    /// ensure-host task faulted with it. Theme lookups must never throw:
    /// TryGetValue + a neutral gray fallback keeps the mirror alive even if
    /// the macOS token set is ever renamed.
    /// </summary>
    private static Brush ThemeBrush(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch { /* resources not ready — fall through to the neutral brush */ }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
    }

    private StageRow BuildStageRow(BootStage s)
    {
        var item = new BootStageItem(s.Id, s.Title) { IsDone = s.Completed };
        var glyph = new FontIcon
        {
            Glyph = item.Glyph,
            FontSize = 15,
            Foreground = ThemeBrush("MacAccentBrush"),
        };
        var title = new TextBlock
        {
            Text = s.Title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("MacLabelBrush"),
        };
        var detail = new TextBlock
        {
            Text = s.Detail ?? string.Empty,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Foreground = ThemeBrush("MacLabel2Brush"),
        };
        var root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 3, 0, 3) };
        root.Children.Add(glyph);
        root.Children.Add(title);
        root.Children.Add(detail);
        return new StageRow(item, root, glyph, detail);
    }
}
