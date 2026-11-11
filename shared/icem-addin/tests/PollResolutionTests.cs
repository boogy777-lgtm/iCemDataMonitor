using System;
using System.Collections.Generic;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// Com resolution law (production fix) — vendor-free surface: PollResolution.Resolve / TryConvert.
/// WITS tests REMOVED with the wits feature (operator directive).
/// </summary>
public class PollResolutionTests
{
    private static KeyValuePair<string, object> Cand(string name, object value)
    {
        return new KeyValuePair<string, object>(name, value);
    }

    // ── com resolution law ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_BadTypeCandidate_DoesNotConsumeAddressClaim()
    {
        // Root-cause fix: a badtype duplicate must not win the address race —
        // the later good candidate claims it.
        var candidates = new List<KeyValuePair<string, object>>
        {
            Cand("Pump Pressure", DateTime.Now), // sentinel
            Cand("Pump Pressure", 15.5f),        // good
        };

        var outcome = PollResolution.Resolve(candidates);

        Assert.Single(outcome.Entries);
        Assert.Equal(16, outcome.Entries[0].Address); // v5: idx 6 → 4 + 12
        Assert.Equal(15.5f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal(1, outcome.BadType);
        Assert.Equal(0, outcome.Dup);
        Assert.Contains("Pump Pressure", outcome.BadTypeNames);
    }

    [Fact]
    public void Resolve_FirstConvertibleWins_DedupDropsLaterAliases()
    {
        var candidates = new List<KeyValuePair<string, object>>
        {
            Cand("Pump Pressure", 1.0f),   // first convertible claims address 16 (v5: 4+idx*2)
            Cand("PS Pump Press", 2.0f),   // alias to the same address → dropped
        };

        var outcome = PollResolution.Resolve(candidates);

        Assert.Single(outcome.Entries);
        Assert.Equal(16, outcome.Entries[0].Address);
        Assert.Equal(1.0f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal(1, outcome.Dup);
        Assert.Equal(0, outcome.BadType);
    }

    [Theory]
    [InlineData(typeof(DateTime), false)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(bool), false)]
    public void TryConvert_NonNumericTypes_ReturnFalse(Type type, bool _)
    {
        object raw = type == typeof(DateTime) ? DateTime.Now : (type == typeof(string) ? (object)"1.5" : (object)true);
        double value;
        Assert.False(PollResolution.TryConvert(raw, out value));
    }

    [Fact]
    public void TryConvert_NumericTypes_ReturnTrue()
    {
        double value;
        Assert.True(PollResolution.TryConvert(15.5f, out value));
        Assert.Equal(15.5, value, 5);
        Assert.True(PollResolution.TryConvert((byte)7, out value));
        Assert.Equal(7.0, value, 5);
        Assert.True(PollResolution.TryConvert(42m, out value));
        Assert.Equal(42.0, value, 5);
        Assert.False(PollResolution.TryConvert(null, out value));
        Assert.False(PollResolution.TryConvert(double.NaN, out value));
        Assert.False(PollResolution.TryConvert(double.PositiveInfinity, out value));
    }

    // ── lan resolution law (bus register map v5: lan_stream_crio map) ─────────

    [Fact]
    public void ResolveLan_FirstConvertibleWins_LanAddresses()
    {
        var candidates = new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 125.93f),   // idx 33 → 200 + 66 = 266
            Cand("PumpAPressure", 99.0f),       // normalized alias → same address → dropped
            Cand("Relay Out", "text"),          // badtype → does NOT claim
        };

        var outcome = PollResolution.ResolveLan(candidates);

        Assert.Single(outcome.Entries);
        Assert.Equal(266, outcome.Entries[0].Address);
        Assert.Equal(125.93f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal(1, outcome.Dup);
        Assert.Equal(1, outcome.BadType);
    }

    [Fact]
    public void ResolveLan_NamesOutsideCuratedView_AreDropped()
    {
        var candidates = new List<KeyValuePair<string, object>>
        {
            Cand("Truck Number", 1.0f),   // idx >= 96 — out of the curated view
        };

        var outcome = PollResolution.ResolveLan(candidates);

        Assert.Empty(outcome.Entries);
        Assert.Equal(0, outcome.BadType);   // silently dropped at the lookup, ¬ badtype
    }

    // ── INC-13 batch law: fresh-first tie-break among duplicate LAN sources ──
    //
    // Stand evidence: 10 LAN sources expose overlapping names; vendor gates
    // EquipmentDataWriter.IsRecording (ServiceVectorInfo_EventReceived) freeze
    // non-recording units' holders, so first-convertible-wins can pin an
    // address to a frozen source while the live unit's values count away as
    // dup. The batch law: first FRESH convertible wins; a fresh candidate
    // replaces a frozen claim; no fresh candidate ⇒ legacy law (fallback).

    private static PollResolution.LanSourceBatch Batch(string key, bool fresh, params KeyValuePair<string, object>[] cands)
    {
        var batch = new PollResolution.LanSourceBatch { SourceKey = key, Fresh = fresh };
        batch.Candidates.AddRange(cands);
        return batch;
    }

    [Fact]
    public void ResolveLan_Batches_FrozenEarlierSource_LiveLaterWins()
    {
        // THE mis-pick repair: a frozen (LOST/non-recording) unit listed BEFORE
        // the live unit must not pin the address anymore.
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer2", false, Cand("Pump A Pressure", 1.0f)),   // frozen, earlier
            Batch("Cementer1", true,  Cand("Pump A Pressure", 2.0f)),   // live, later
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(266, outcome.Entries[0].Address);
        Assert.Equal(2.0f, (float)outcome.Entries[0].Value, 3);     // LIVE value wins
        Assert.Equal("Cementer1", outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);                               // displaced frozen candidate
        Assert.Equal(0, outcome.BadType);
        Assert.Equal(1, outcome.WinsBySource["Cementer1"]);
        Assert.False(outcome.WinsBySource.ContainsKey("Cementer2"));
    }

    [Fact]
    public void ResolveLan_Batches_AllFrozen_LegacyFirstConvertibleFallback()
    {
        // Owner-accepted compromise pinned: when freshness cannot differentiate
        // (no source fresh), the legacy first-convertible-wins law applies.
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer2", false, Cand("Pump A Pressure", 1.0f)),
            Batch("Cementer1", false, Cand("Pump A Pressure", 2.0f)),
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(1.0f, (float)outcome.Entries[0].Value, 3);     // first-convertible, unchanged
        Assert.Equal("Cementer2", outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);
    }

    [Fact]
    public void ResolveLan_Batches_FirstFreshWins_LaterFrozenDoesNotSteal()
    {
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer1", true,  Cand("Pump A Pressure", 1.0f)),
            Batch("Cementer2", false, Cand("Pump A Pressure", 2.0f)),
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(1.0f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal("Cementer1", outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);
    }

    [Fact]
    public void ResolveLan_Batches_FirstFreshSourceWins_AmongMultipleFresh()
    {
        // No churn among fresh sources: the FIRST fresh claim sticks.
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer1", true,  Cand("Pump A Pressure", 1.0f)),
            Batch("Cementer3", true,  Cand("Pump A Pressure", 3.0f)),
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(1.0f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal("Cementer1", outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);
    }

    [Fact]
    public void ResolveLan_Batches_BadTypeNeverClaims_FreshStillUpgrades()
    {
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer2", false, Cand("Pump A Pressure", 1.0f)),      // frozen claims
            Batch("Cementer9", true,  Cand("Pump A Pressure", DateTime.Now)), // badtype — no claim, no upgrade
            Batch("Cementer1", true,  Cand("Pump A Pressure", 3.0f)),      // fresh upgrade
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(3.0f, (float)outcome.Entries[0].Value, 3);
        Assert.Equal("Cementer1", outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);
        Assert.Equal(1, outcome.BadType);
    }

    [Fact]
    public void ResolveLan_Batches_WinsBySource_SplitAcrossSources()
    {
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer2", false, Cand("Pump A Pressure", 1.0f)),      // frozen claim @266
            Batch("Cementer3", false, Cand("Pump B Pressure", 2.0f)),      // frozen claim @282 (unique)
            Batch("Cementer1", true,  Cand("Pump A Pressure", 9.0f)),      // fresh upgrade @266
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Equal(2, outcome.Entries.Count);
        Assert.Equal(9.0f, (float)outcome.Entries[0].Value, 3);            // 266 upgraded to live
        Assert.Equal("Cementer1", outcome.Entries[0].SourceKey);
        Assert.Equal(2.0f, (float)outcome.Entries[1].Value, 3);            // 282 untouched
        Assert.Equal("Cementer3", outcome.Entries[1].SourceKey);
        Assert.Equal(1, outcome.Dup);
        Assert.Equal(1, outcome.WinsBySource["Cementer1"]);
        Assert.Equal(1, outcome.WinsBySource["Cementer3"]);
        Assert.False(outcome.WinsBySource.ContainsKey("Cementer2"));       // displaced = zero wins
    }

    [Fact]
    public void ResolveLan_FlatList_StillLegacy_FirstConvertibleWins()
    {
        // The flat overload (COM-parallel path shape) = one non-fresh batch —
        // legacy law pinned explicitly, SourceKey null.
        var candidates = new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 1.0f),
            Cand("PumpAPressure", 9.0f),   // alias, same address
        };

        var outcome = PollResolution.ResolveLan(candidates);

        Assert.Single(outcome.Entries);
        Assert.Equal(1.0f, (float)outcome.Entries[0].Value, 3);
        Assert.Null(outcome.Entries[0].SourceKey);
        Assert.Equal(1, outcome.Dup);
    }

    [Fact]
    public void ResolveLan_Batches_NamesOutsideMap_DroppedSilently()
    {
        var batches = new List<PollResolution.LanSourceBatch>
        {
            Batch("Cementer1", true, Cand("Truck Number", 1.0f)),
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Empty(outcome.Entries);
        Assert.Equal(0, outcome.BadType);
        Assert.Equal(0, outcome.Dup);
        Assert.Empty(outcome.WinsBySource);
    }

    [Fact]
    public void ResolveLan_Batches_NullAndEmptyBatches_AreSkipped()
    {
        var batches = new List<PollResolution.LanSourceBatch>
        {
            null,
            Batch("Cementer1", true, Cand("Pump A Pressure", 5.0f)),
        };

        var outcome = PollResolution.ResolveLan(batches);

        Assert.Single(outcome.Entries);
        Assert.Equal(266, outcome.Entries[0].Address);
        Assert.Equal(5.0f, (float)outcome.Entries[0].Value, 3);
    }
}
