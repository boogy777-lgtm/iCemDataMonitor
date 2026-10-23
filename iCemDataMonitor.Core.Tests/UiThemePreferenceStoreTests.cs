using iCemDataMonitor.Core.Config;
using Xunit;

namespace iCemDataMonitor.Core.Tests;

public sealed class UiThemePreferenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "theme_" + Guid.NewGuid().ToString("N"));
    public UiThemePreferenceStoreTests() => Directory.CreateDirectory(_dir);
    private string PathOf(string name = "ui-theme") => Path.Combine(_dir, name);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void MissingMalformedAndTrimmedTokens()
    {
        var store = new UiThemePreferenceStore(PathOf());
        Assert.False(store.Read());
        foreach (var text in new[] { "LIGHT", "false", "", "other" })
        {
            File.WriteAllText(PathOf(), text);
            Assert.False(store.Read());
        }
        File.WriteAllText(PathOf(), " light\r\n");
        Assert.True(store.Read());
    }

    [Fact]
    public void CommitNotifiesAfterCompleteTextAndNotifyFailureKeepsCommit()
    {
        var pokes = 0;
        var logs = new List<string>();
        var store = new UiThemePreferenceStore(PathOf(), () =>
        {
            Assert.Equal("light", File.ReadAllText(PathOf()));
            pokes++;
            throw new IOException("poke fault");
        }, logs.Add);
        var result = store.Write(true);
        Assert.True(result.Written);
        Assert.True(result.CurrentLight);
        Assert.Null(result.Error);
        Assert.Equal(1, pokes);
        Assert.Contains(logs, x => x.Contains("poke FAILED"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("collision")]
    [InlineData("parent-file")]
    [InlineData("invalid")]
    public void FaultDoesNotPokeAndRecoveryWorks(string fault)
    {
        var path = PathOf();
        if (fault == "missing") path = PathOf("absent/ui-theme");
        if (fault == "collision") Directory.CreateDirectory(path);
        if (fault == "parent-file") { File.WriteAllText(PathOf("parent"), "keep"); path = PathOf("parent/ui-theme"); }
        if (fault == "invalid") path = PathOf("bad\0path");
        var pokes = 0;
        var store = new UiThemePreferenceStore(path, () => pokes++);
        var result = store.Write(true);
        Assert.False(result.Written);
        Assert.False(result.CurrentLight);
        Assert.NotNull(result.Error);
        Assert.Equal(0, pokes);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        if (fault == "collision")
        {
            Directory.Delete(path);
            Assert.True(store.Write(true).Written);
            Assert.Equal(1, pokes);
        }
    }

    [Fact]
    public async Task ConcurrentWritesAlwaysPublishCompleteToken()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => new UiThemePreferenceStore(PathOf())).ToArray();
        File.WriteAllText(PathOf(), "dark");
        var written = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            if (stores[i % 4].Write(i % 2 == 0).Written) Interlocked.Increment(ref written);
            using var stream = new FileStream(PathOf(), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.Contains(reader.ReadToEnd(), new[] { "light", "dark" });
        })));
        Assert.True(written > 0);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void LegacyAdoptionUsesStationProjectionAndPreservesSource()
    {
        var legacy = PathOf("mac-theme");
        File.WriteAllText(legacy, "light");
        Assert.True(new UiThemePreferenceStore(PathOf()).ReadWithLegacyAdoption(legacy));
        File.WriteAllText(PathOf(), "dark");
        Assert.False(new UiThemePreferenceStore(PathOf()).ReadWithLegacyAdoption(legacy));
        Assert.False(new UiThemePreferenceStore(PathOf("missing/ui-theme")).ReadWithLegacyAdoption(legacy));
        Assert.Equal("light", File.ReadAllText(legacy));
    }

    [Fact]
    public void WindowsLockedOrReadonlyTargetPreservesOldValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(PathOf(), "dark");
        var pokes = 0;
        var store = new UiThemePreferenceStore(PathOf(), () => pokes++);
        using (var held = new FileStream(PathOf(), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(store.Write(true).Written);
        File.SetAttributes(PathOf(), FileAttributes.ReadOnly);
        try { Assert.False(store.Write(true).Written); }
        finally { File.SetAttributes(PathOf(), FileAttributes.Normal); }
        Assert.Equal("dark", File.ReadAllText(PathOf()));
        Assert.Equal(0, pokes);
        Assert.True(store.Write(true).Written);
    }
}
