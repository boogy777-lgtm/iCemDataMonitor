using System;
using System.Collections.Generic;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// ZoneLiveness FSM — TOTAL transition table (statechart-as-pattern: every
/// state × event pair = one row; the Э2b rows B02/B09/B13/B17..B20 are the same
/// function in CSV form). ONE machine, THREE instances in the module (com/lan/raw).
/// </summary>
public class ZoneLivenessTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly DateTime T0 = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> TransitionRows()
    {
        // rowId, startActive, eventKind ("mark"|"poll"), silenceSeconds, expectActive, expectPost
        yield return new object[] { "T1_Idle_Mark", false, "mark", 0.0, true, false };
        yield return new object[] { "T2_Idle_Poll", false, "poll", 3600.0, false, false };
        yield return new object[] { "T3_Active_Mark", true, "mark", 0.0, true, false };
        yield return new object[] { "T4_Active_Poll_InsideWindow", true, "poll", 4.999, true, false };
        yield return new object[] { "T5_Active_Poll_Boundary", true, "poll", 5.0, true, false };   // == timeout is NOT beyond
        yield return new object[] { "T6_Active_Poll_Beyond", true, "poll", 5.001, false, true };  // Active→Idle: post once
    }

    [Theory]
    [MemberData(nameof(TransitionRows))]
    public void TransitionTable_Total(string rowId, bool startActive, string eventKind,
        double silenceSeconds, bool expectActive, bool expectPost)
    {
        var fsm = new ZoneLiveness(ZoneKind.Lan);
        if (startActive)
        {
            fsm.MarkWritten(T0);
        }

        bool post = false;
        if (eventKind == "mark")
        {
            fsm.MarkWritten(T0.AddSeconds(silenceSeconds));
        }
        else
        {
            post = fsm.Poll(T0.AddSeconds(silenceSeconds), Idle);
        }

        Assert.True(expectActive == fsm.IsActive, $"{rowId}: IsActive {fsm.IsActive} != expected {expectActive}");
        Assert.True(expectPost == post, $"{rowId}: post {post} != expected {expectPost}");
    }

    [Fact]
    public void MarkWritten_RefreshesEvidence()
    {
        var fsm = new ZoneLiveness(ZoneKind.ComMain);
        fsm.MarkWritten(T0);
        fsm.MarkWritten(T0.AddSeconds(3)); // flowing stream — evidence moves
        Assert.Equal(T0.AddSeconds(3), fsm.LastWriteUtc);
        // 4 s after the LAST write: still active (window measured from evidence, ¬ first write)
        Assert.False(fsm.Poll(T0.AddSeconds(7), Idle));
        Assert.True(fsm.IsActive);
    }

    [Fact]
    public void Drop_HappensExactlyOnce_PerCrossing()
    {
        var fsm = new ZoneLiveness(ZoneKind.RawReserve);
        fsm.MarkWritten(T0);
        Assert.True(fsm.Poll(T0.AddSeconds(6), Idle));    // the crossing: post once
        Assert.False(fsm.IsActive);
        Assert.False(fsm.Poll(T0.AddSeconds(7), Idle));   // no repeated posts — no bounce
        Assert.False(fsm.Poll(T0.AddSeconds(100), Idle));
    }

    [Fact]
    public void Reconnect_ReArms_AfterDrop()
    {
        var fsm = new ZoneLiveness(ZoneKind.Lan);
        fsm.MarkWritten(T0);
        Assert.True(fsm.Poll(T0.AddSeconds(6), Idle));    // drop
        fsm.MarkWritten(T0.AddSeconds(10));               // reconnect: write re-enters Active
        Assert.True(fsm.IsActive);
        Assert.False(fsm.Poll(T0.AddSeconds(14), Idle));  // flap silence < 5 s: no bounce
        Assert.True(fsm.Poll(T0.AddSeconds(16), Idle));   // next crossing posts again
        Assert.False(fsm.IsActive);
    }
}
