using System;
using System.Collections.Generic;
using System.Globalization;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// INC-13 LANSRC diagnostics: per-LAN-source changed-value counting (the
/// AddIn-local freshness evidence) + readout formatting. Vendor-free —
/// the module feeds raw candidate snapshots, the tracker counts bit changes.
/// </summary>
public class LanSourceTrackerTests
{
    private static KeyValuePair<string, object> Cand(string name, object value)
    {
        return new KeyValuePair<string, object>(name, value);
    }

    private static DateTime T0 = new DateTime(2026, 8, 30, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Observe_FirstSighting_CountsAsChanged()
    {
        var tracker = new LanSourceTracker("eq=Cementer 1|unit=0x00A86F46");
        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 1.0f),
            Cand("Pump B Pressure", 2.0f),
            Cand("Unit Num", 3.0f),
        });

        Assert.Equal(3, tracker.Matched);
        Assert.Equal("3/0s", tracker.ReadAndResetChanged(T0));
    }

    [Fact]
    public void Observe_SameBoxedValues_AreStable_ZeroChanges()
    {
        // The frozen-source signature: byte-identical values every tick.
        var tracker = new LanSourceTracker("k");
        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 125.93f),
            Cand("Pump B Pressure", 1.0f),
        });
        tracker.ReadAndResetChanged(T0);

        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 125.93f),
            Cand("Pump B Pressure", 1.0f),
        });
        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 125.93f),
            Cand("Pump B Pressure", 1.0f),
        });

        Assert.Equal("0/0s", tracker.ReadAndResetChanged(T0));
    }

    [Fact]
    public void Observe_ValueChanged_CountsOnce_PerTick()
    {
        var tracker = new LanSourceTracker("k");
        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 1.0f),
            Cand("Pump B Pressure", 2.0f),
        });
        tracker.ReadAndResetChanged(T0);

        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 5.0f),   // changed
            Cand("Pump B Pressure", 2.0f),   // frozen
        });
        tracker.Observe(new List<KeyValuePair<string, object>>
        {
            Cand("Pump A Pressure", 5.0f),   // held
            Cand("Pump B Pressure", 2.0f),
        });

        Assert.Equal("1/0s", tracker.ReadAndResetChanged(T0));
    }

    [Fact]
    public void Observe_NullToValue_AndValueToNull_CountAsChanges()
    {
        var tracker = new LanSourceTracker("k");
        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", null) });
        tracker.ReadAndResetChanged(T0);

        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", 7.0f) });
        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", null) });

        Assert.Equal("2/0s", tracker.ReadAndResetChanged(T0));
    }

    [Fact]
    public void ReadAndResetChanged_ReportsElapsedSeconds_OfTheAccumulationWindow()
    {
        var tracker = new LanSourceTracker("k");
        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", 1.0f) });
        Assert.Equal("1/0s", tracker.ReadAndResetChanged(T0));       // first window: epoch unknown

        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", 2.0f) });
        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", 3.0f) });
        Assert.Equal("2/60s", tracker.ReadAndResetChanged(T0.AddSeconds(60)));  // owner format changed=N/60s

        tracker.Observe(new List<KeyValuePair<string, object>> { Cand("Pump A Pressure", 3.0f) });
        Assert.Equal("0/30s", tracker.ReadAndResetChanged(T0.AddSeconds(90)));  // 0 changes over an honest 30 s window
    }

    [Fact]
    public void Observe_NameCap_NewNamesBeyondCap_AreIgnored()
    {
        var tracker = new LanSourceTracker("k");
        var full = new List<KeyValuePair<string, object>>();
        for (int i = 0; i < LanSourceTracker.MaxTrackedNames; i++)
        {
            full.Add(Cand("name" + i.ToString(CultureInfo.InvariantCulture), (float)i));
        }
        tracker.Observe(full);
        tracker.ReadAndResetChanged(T0);

        // same values again → 0 changes (all within cap)
        tracker.Observe(full);
        Assert.Equal("0/0s", tracker.ReadAndResetChanged(T0));

        // one MORE name (beyond cap) + one changed in-cap name → only the in-cap change counts
        var overflow = new List<KeyValuePair<string, object>>(full) { Cand("overflow", 1.0f) };
        overflow[0] = Cand("name0", 999.0f);
        tracker.Observe(overflow);
        Assert.Equal("1/0s", tracker.ReadAndResetChanged(T0));
        Assert.Equal(LanSourceTracker.MaxTrackedNames + 1, tracker.Matched); // matched reflects the snapshot, cap applies to memory only
    }

    [Fact]
    public void IdentityFields_CarryThrough()
    {
        var tracker = new LanSourceTracker("Cementer 1|Cementer|0x00A86F46")
        {
            Equipment = "Cementer 1",
            UnitNumber = "0x00A86F46",
            RecordName = "Cementer",
            Online = true,
            Fresh = true,
            AgeSeconds = 0.4,
            Won = 32,
        };

        Assert.True(tracker.Fresh);
        Assert.Equal("0x00A86F46", tracker.UnitNumber);
        Assert.Equal(32, tracker.Won);
    }
}
