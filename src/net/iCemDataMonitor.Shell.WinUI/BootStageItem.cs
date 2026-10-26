using ReactiveUI;

namespace iCemDataMonitor.Shell.WinUI;

/// <summary>
/// One splash checkbox row (ReactiveObject per the VM whitelist). Lives in its
/// own file so the XAML compiler can resolve the x:DataType reference.
/// </summary>
public class BootStageItem : ReactiveObject
{
    private bool _isDone;
    private string _detail = string.Empty;

    public string Id { get; }
    public string Title { get; }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDone, value);
            this.RaisePropertyChanged(nameof(Glyph)); // checkbox glyph flips with it
        }
    }

    public string Detail
    {
        get => _detail;
        set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    /// <summary>Segoe MDL2 checkbox glyphs: E73A checked, E739 empty.</summary>
    public string Glyph => IsDone ? "\uE73A" : "\uE739";

    public BootStageItem(string id, string title)
    {
        Id = id;
        Title = title;
    }
}
