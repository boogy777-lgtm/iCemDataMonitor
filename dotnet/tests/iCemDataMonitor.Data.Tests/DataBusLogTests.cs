using iCemDataMonitor.Data;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// DataBusLog contract tests (owner log policy 2026-08-26): ProgramData
/// evidence channel via the Sdk canon StationFileLog — dedup law, re-arm on
/// success, never-throw, kill switch. Tests redirect the directory to a temp
/// location (§15.7(2a) data-seam: paths are DATA) and assert with UNIQUE
/// markers so concurrent test classes logging through the disabled/other
/// channel cannot interfere.
/// </summary>
[Collection("DataBusLogState")] // serialized against BusOpenTruthTableTests:
// its reader-error paths emit DataBusLog.Error lines — the shared static
// channel (dedup gate, dir override) is per-test state here, and parallel
// lines would race the File.ReadAllText asserts below.
public sealed class DataBusLogTests : IDisposable
{
    private readonly string _dir;

    public DataBusLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "databuslog_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string LogPath => Path.Combine(_dir, "data-bus.log");

    /// <summary>Enable the channel for ONE test body (the assembly-wide module
    /// initializer keeps it OFF so foreign test classes never write ProgramData).</summary>
    private IDisposable Enable() => new ChannelEnabler(this);

    private sealed class ChannelEnabler : IDisposable
    {
        private readonly bool _wasEnabled;
        private readonly bool? _wasOverride;

        public ChannelEnabler(DataBusLogTests self)
        {
            _wasEnabled = DataBusLog.Enabled;
            _wasOverride = DataBusLog.TestContextOverride;
            DataBusLog.Enabled = true;
            DataBusLog.TestContextOverride = false; // force the channel ON under testhost
            DataBusLog.LogDirOverride = self._dir;
        }

        public void Dispose()
        {
            DataBusLog.Enabled = _wasEnabled;
            DataBusLog.TestContextOverride = _wasOverride;
            DataBusLog.LogDirOverride = null;
        }
    }

    [Fact]
    public void Error_WritesTimestampedLine_UnderOverrideDir()
    {
        const string marker = "DBL-marker-alpha";
        using var _ = Enable();
        {
            DataBusLog.Error(marker);

            var text = File.ReadAllText(LogPath);
            Assert.Contains(marker, text);
            // Canon TapLog line format: UTC ISO-8601 "O" prefix + space + line.
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", text);
        }
    }

    [Fact]
    public void Error_DedupSameLine_OnceUntilDistinctOrRearm()
    {
        const string marker = "DBL-marker-bravo";
        using var _ = Enable();
        {
            DataBusLog.Error(marker);
            DataBusLog.Error(marker);
            DataBusLog.Error(marker); // 1 Hz poller retries — must land ONCE

            Assert.Equal(1, CountMarker(marker));

            // A DIFFERENT line is recorded immediately (state changed).
            DataBusLog.Error(marker + "-2");
            Assert.Equal(1, CountMarker(marker));
            Assert.Equal(1, CountMarker(marker + "-2"));

            // Success re-arms: the same failure logs again after recovery.
            DataBusLog.Success();
            DataBusLog.Error(marker);
            Assert.Equal(2, CountMarker(marker));
        }
    }

    [Fact]
    public void Error_NeverThrows_OnNullEmptyOrIllegalPathChars()
    {
        using var _ = Enable();
        {
            DataBusLog.Error(null!);               // no-op by contract
            DataBusLog.Error(string.Empty);        // no-op by contract
            DataBusLog.Error(new string('\0', 8)); // illegal in a path — swallowed downstream

            // The channel itself stays alive for legal lines.
            DataBusLog.Error("DBL-marker-charlie");
            Assert.Contains("DBL-marker-charlie", File.ReadAllText(LogPath));
        }
    }

    [Fact]
    public void FailedSink_IsSwallowed_AndDedupRequiresSuccessToRearm()
    {
        const string marker = "DBL-io-fault-rearm";
        using var enabled = Enable();
        DataBusLog.Success();
        DataBusLog.LogDirOverride = "\0illegal";
        DataBusLog.Error(marker); // path failure must never escape into the bus caller
        DataBusLog.LogDirOverride = _dir;
        DataBusLog.Error(marker); // failed best-effort write still participates in dedup
        Assert.False(File.Exists(LogPath));
        DataBusLog.Success();
        DataBusLog.Error(marker);
        Assert.Equal(1, CountMarker(marker));
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        const string marker = "DBL-marker-delta";
        var wasEnabled = DataBusLog.Enabled;
        DataBusLog.Enabled = false;
        try
        {
            DataBusLog.LogDirOverride = _dir;
            try
            {
                DataBusLog.Error(marker);
                Assert.False(File.Exists(LogPath), "the kill switch must suppress all writes");
            }
            finally
            {
                DataBusLog.LogDirOverride = null;
            }
        }
        finally
        {
            DataBusLog.Enabled = wasEnabled; // module initializer default = false
        }
    }

    [Fact]
    public void TestHostContext_AutoGuards_WritesNothing_EvenWhenEnabled()
    {
        const string marker = "DBL-marker-echo";
        var wasEnabled = DataBusLog.Enabled;
        var wasOverride = DataBusLog.TestContextOverride;
        DataBusLog.Enabled = true;          // channel ON…
        DataBusLog.TestContextOverride = null; // …but auto-detect must block under testhost
        try
        {
            DataBusLog.LogDirOverride = _dir;
            try
            {
                DataBusLog.Error(marker);
                Assert.False(File.Exists(LogPath),
                    "the generic test-context guard must suppress writes under dotnet test");
            }
            finally
            {
                DataBusLog.LogDirOverride = null;
            }
        }
        finally
        {
            DataBusLog.Enabled = wasEnabled;
            DataBusLog.TestContextOverride = wasOverride;
        }
    }

    /// <summary>Log LINES ending with exactly this marker (0 when absent) —
    /// line-scoped so a longer sibling marker ("X-2") never counts as "X".</summary>
    private int CountMarker(string marker)
    {
        if (!File.Exists(LogPath))
            return 0;
        return File.ReadAllLines(LogPath)
            .Count(l => l.EndsWith(marker, StringComparison.Ordinal));
    }
}
