using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

public class DataMonitorAddressMapTests
{
    [Theory]
    // LAYOUT v5: com_stream addr = 4 + index*2 (the v4 rebase/skip rule is GONE).
    [InlineData("Time & Date", 4)]      // idx 0
    [InlineData("UnixTimestamp", 4)]    // idx 0 (alias)
    [InlineData("Unit Num", 6)]         // idx 1
    [InlineData("Slurry Density", 8)]   // idx 2 (tapName)
    [InlineData("SlurryDensity", 8)]    // idx 2 (canonical)
    [InlineData("Recirc Density", 8)]   // idx 2 (flecs alias — com map side)
    [InlineData("Pump Pressure", 16)]   // idx 6
    [InlineData("PS Pump Press", 16)]   // idx 6 (alias)
    [InlineData("DS Pump Press", 18)]   // idx 7 (wireName)
    [InlineData("Dig out", 36)]         // idx 16
    [InlineData("DS Pmp Stg Tot", 68)]  // idx 32
    [InlineData("VolumetricMeter", 70)] // idx 33
    public void TryGetComAddress_MapsRegistryKeys(string name, int expectedAddress)
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetComAddress(name, out address), name);
        Assert.Equal(expectedAddress, address);
    }

    [Theory]
    // LAYOUT v5: lan_stream_crio addr = 200 + index*2 — the 96 cRIO signals of
    // the observed ACE superframe (Wireshark/PROTOCOL.md curated view).
    [InlineData("Recirc Density", 200)]        // idx 0 (0x13120200)
    [InlineData("Recirc Flow Rate", 202)]      // idx 1
    [InlineData("Downhole Density", 206)]      // idx 3
    [InlineData("Mix Water Rate", 208)]        // idx 4
    [InlineData("Auto or Manual Mix", 220)]    // idx 10
    [InlineData("Cement Vlv Cmd", 226)]        // idx 13
    [InlineData("Unit Number", 254)]           // idx 27 (0x13120203)
    [InlineData("Mix Mode", 258)]              // idx 29
    [InlineData("Pump A Pressure", 266)]       // idx 33 (0x13120206)
    [InlineData("Pump B Pressure", 282)]       // idx 41
    [InlineData("Relay Out", 306)]             // idx 53 (0x13120209)
    [InlineData("Cmnt Ops Track IDNum", 388)]  // idx 94 (0x1312020F)
    [InlineData("Cmnt Ops Track Descript", 390)] // idx 95 (0x1312020F)
    public void TryGetLanAddress_MapsRegistryKeys(string name, int expectedAddress)
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetLanAddress(name, out address), name);
        Assert.Equal(expectedAddress, address);
    }

    // ── LAN VENDOR ALIASES (additive-only, user dispatch 2026-08-18) ──────────
    // The 73 abbreviated cRIO display names the vendor LAN source actually
    // exposes (live POLLUNIV harvest — specs/FlecsEmu/Э2c_lan_zone_classification
    // .csv, audit 2026-08-18). Every alias resolves to the EXISTING
    // addr=200+idx*2 entry (rule n+1: never a new address, never a moved
    // entry); the generator asserts uniqueness at generation time.
    public static System.Collections.Generic.IEnumerable<object[]> VendorAliasRows()
    {
        // (vendor name, addr = 200 + idx*2)
        yield return new object[] { "Rcrc Density", 200 };       // idx 0
        yield return new object[] { "Recirc Fl Rte", 202 };      // idx 1
        yield return new object[] { "Rec Vlv Cmd", 204 };        // idx 2
        yield return new object[] { "Dwnhole Density", 206 };    // idx 3
        yield return new object[] { "Mix Wtr Stg Tot", 210 };    // idx 5
        yield return new object[] { "Mix Wtr Job Tot", 212 };    // idx 6
        yield return new object[] { "Dwnhole Tub Lvl", 214 };    // idx 7
        yield return new object[] { "Mix Pump Pres", 216 };      // idx 8
        yield return new object[] { "Mix Water Pres", 218 };     // idx 9
        yield return new object[] { "Auto or Man Mix", 220 };    // idx 10
        yield return new object[] { "Cmnt Vlv Cmd", 226 };       // idx 13
        yield return new object[] { "Vol Water %", 230 };        // idx 15
        yield return new object[] { "Vol Mtr Rate", 232 };       // idx 16
        yield return new object[] { "Vol Mtr Stg Tot", 234 };    // idx 17
        yield return new object[] { "Vol Mtr Job Tot", 236 };    // idx 18
        yield return new object[] { "Stage Sacks Cmt", 238 };    // idx 19
        yield return new object[] { "Job Sacks Cmt", 240 };      // idx 20
        yield return new object[] { "Mud Cup Dens", 242 };       // idx 21
        yield return new object[] { "CurBld Density", 244 };     // idx 22
        yield return new object[] { "CurBld Yield", 246 };       // idx 23
        yield return new object[] { "CurBld Wtr Req", 248 };     // idx 24
        yield return new object[] { "CurBld Mix Rate", 250 };    // idx 25
        yield return new object[] { "Unit Num", 254 };           // idx 27 (lan side)
        yield return new object[] { "Raw Pmp A Press", 268 };    // idx 34
        yield return new object[] { "SideA Pr At KO", 272 };     // idx 36
        yield return new object[] { "SideA KO?", 274 };          // idx 37
        yield return new object[] { "Pump A Stg Tot", 278 };     // idx 39
        yield return new object[] { "Pump A Job Tot", 280 };     // idx 40
        yield return new object[] { "Raw Pmp B Press", 284 };    // idx 42
        yield return new object[] { "SideB Pr At KO", 288 };     // idx 44
        yield return new object[] { "SideB KO?", 290 };          // idx 45
        yield return new object[] { "Pump B Stg Tot", 294 };     // idx 47
        yield return new object[] { "Pump B Job Tot", 296 };     // idx 48
        yield return new object[] { "Cmb Pump Rate", 298 };      // idx 49
        yield return new object[] { "Cmb Stg Total", 300 };      // idx 50
        yield return new object[] { "Cmb Job Total", 302 };      // idx 51
        yield return new object[] { "Highest Pmp P", 304 };      // idx 52
        yield return new object[] { "Digital In", 308 };         // idx 54
        yield return new object[] { "Digital Out", 310 };        // idx 55
        yield return new object[] { "Display Temp", 312 };       // idx 56
        yield return new object[] { "Controller Temp", 314 };    // idx 57
        yield return new object[] { "Analog In 1", 316 };        // idx 58
        yield return new object[] { "Analog In 2", 318 };        // idx 59
        yield return new object[] { "Analog In 3", 320 };        // idx 60
        yield return new object[] { "Analog In 4", 322 };        // idx 61
        yield return new object[] { "Analog In 5", 324 };        // idx 62
        yield return new object[] { "Analog In 6", 326 };        // idx 63
        yield return new object[] { "Analog In 7", 328 };        // idx 64
        yield return new object[] { "Analog In 8", 330 };        // idx 65
        yield return new object[] { "Analog Out 1", 332 };       // idx 66
        yield return new object[] { "Analog Out 2", 334 };       // idx 67
        yield return new object[] { "Analog Out 3", 336 };       // idx 68
        yield return new object[] { "Analog Out 4", 338 };       // idx 69
        yield return new object[] { "Count In 1", 340 };         // idx 70
        yield return new object[] { "Count In 2", 342 };         // idx 71
        yield return new object[] { "Count In 3", 344 };         // idx 72
        yield return new object[] { "Count In 4", 346 };         // idx 73
        yield return new object[] { "Count In 5", 348 };         // idx 74
        yield return new object[] { "Count In 6", 350 };         // idx 75
        yield return new object[] { "Spare Count 1", 360 };      // idx 80
        yield return new object[] { "Spare StgTot1", 362 };      // idx 81
        yield return new object[] { "Spare JobTot1", 364 };      // idx 82
        yield return new object[] { "Spare Count 2", 366 };      // idx 83
        yield return new object[] { "Spare StgTot2", 368 };      // idx 84
        yield return new object[] { "Spare JobTot2", 370 };      // idx 85
        yield return new object[] { "Spare Count 3", 372 };      // idx 86
        yield return new object[] { "Spare StgTot3", 374 };      // idx 87
        yield return new object[] { "Spare JobTot3", 376 };      // idx 88
        yield return new object[] { "Spare Count 4", 378 };      // idx 89
        yield return new object[] { "Spare StgTot4", 380 };      // idx 90
        yield return new object[] { "Spare JobTot4", 382 };      // idx 91
        yield return new object[] { "Op Track IDNum", 388 };     // idx 94
        yield return new object[] { "Cmnt Ops Track", 390 };     // idx 95 (string value — badtype-skip)
    }

    [Theory]
    [MemberData(nameof(VendorAliasRows))]
    public void TryGetLanAddress_VendorAliases_ResolveToExistingAddrs(string vendorName, int expectedAddress)
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetLanAddress(vendorName, out address),
            "vendor alias '" + vendorName + "' must resolve (Э2c harvest)");
        Assert.Equal(expectedAddress, address);
    }

    [Fact]
    public void VendorAliases_DoNotLeakAcrossZones()
    {
        // "Unit Num" exists in BOTH maps — com idx1 → 6, lan idx27 → 254. The
        // maps are per-zone lookups (source affinity splits COM vs LAN) — the
        // lan alias must not disturb the com entry, and vice versa.
        int address;
        Assert.True(DataMonitorAddressMap.TryGetComAddress("Unit Num", out address));
        Assert.Equal(6, address);
        Assert.True(DataMonitorAddressMap.TryGetLanAddress("Unit Num", out address));
        Assert.Equal(254, address);
        // "Recirc Density" is the standing precedent (com alias + lan key).
        Assert.True(DataMonitorAddressMap.TryGetComAddress("Recirc Density", out address));
        Assert.Equal(8, address);
        Assert.True(DataMonitorAddressMap.TryGetLanAddress("Recirc Density", out address));
        Assert.Equal(200, address);
    }

    [Fact]
    public void ComMap_StaysInsideComStreamZone()
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        foreach (var pair in DataMonitorAddressMap.DebugComEntries())
        {
            min = System.Math.Min(min, pair.Value);
            max = System.Math.Max(max, pair.Value);
        }
        Assert.True(min >= BusLayout.ComStreamBaseReg, "min address = " + min);
        Assert.Equal(70, max); // idx 33 → 4 + 66
        Assert.True(max + 1 < BusLayout.LanStreamCrioBaseReg, "map stays inside com_stream zone");
    }

    [Fact]
    public void LanMap_StaysInsideLanStreamCrioZone_AndCovers96Signals()
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        int distinctAddresses = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (var pair in DataMonitorAddressMap.DebugLanEntries())
        {
            min = System.Math.Min(min, pair.Value);
            max = System.Math.Max(max, pair.Value);
            if (seen.Add(pair.Value))
            {
                distinctAddresses++;
            }
        }
        Assert.True(min >= BusLayout.LanStreamCrioBaseReg, "min address = " + min);
        Assert.Equal(390, max); // idx 95 → 200 + 190
        Assert.True(max + 1 < BusLayout.RawComLineBaseReg, "map stays inside lan_stream_crio zone");
        Assert.Equal(96, distinctAddresses); // 96 cRIO signals, idx 0..95
    }

    [Theory]
    // idx >= 96 is OUT of the curated lan view (Truck Number/Node Address svs
    // 0x00020200/0x00040200 — not in the PROTOCOL.md observed superframe).
    [InlineData("Truck Number")]
    [InlineData("Node Address")]
    [InlineData("Aux Parameter1")]
    public void TryGetLanAddress_OutOfCuratedView_ReturnsFalse(string name)
    {
        int address;
        Assert.False(DataMonitorAddressMap.TryGetLanAddress(name, out address));
        Assert.Equal(0, address);
    }

    [Theory]
    // Former wits display names are NOT poll variables — they never map into
    // com_stream or lan_stream_crio (WITS removed; 600+ = ace_full, POC).
    [InlineData("Flow Rate In")]
    [InlineData("Fluid Density In")]
    [InlineData("Water Rate")]
    [InlineData("Total Volume Pumped")]
    public void FormerWitsDisplayNames_AreNotPollVariables(string witsName)
    {
        int address;
        Assert.False(DataMonitorAddressMap.TryGetComAddress(witsName, out address));
        Assert.False(DataMonitorAddressMap.TryGetLanAddress(witsName, out address));
    }

    [Fact]
    public void NeitherMap_LeaksIntoAceFullZone()
    {
        // v5: regs 600+ are ace_full (POC) — no AddIn map entry ever lands there.
        foreach (var pair in DataMonitorAddressMap.DebugComEntries())
        {
            Assert.False(pair.Value >= BusLayout.AceFullBaseReg,
                "com map entry '" + pair.Key + "' leaks into ace_full at " + pair.Value);
        }
        foreach (var pair in DataMonitorAddressMap.DebugLanEntries())
        {
            Assert.False(pair.Value >= BusLayout.AceFullBaseReg,
                "lan map entry '" + pair.Key + "' leaks into ace_full at " + pair.Value);
        }
    }

    [Fact]
    public void Lookups_AreCaseInsensitive()
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetComAddress("pump pressure", out address));
        Assert.Equal(16, address); // idx 6 → 4 + 12
        Assert.True(DataMonitorAddressMap.TryGetLanAddress("pump a pressure", out address));
        Assert.Equal(266, address); // idx 33 → 200 + 66
    }

    [Theory]
    // DEFECT-1: normalization fallback (ASCII alnum-only, lowercase) makes the
    // maps invariant to whitespace/punctuation/symbol drift on both sides.
    [InlineData("Pump Pressure", 16)]          // exact tapName (fast path)
    [InlineData("PumpPressure", 16)]           // canonical
    [InlineData("  pUmP   PrEsSuRe ", 16)]     // case + whitespace drift
    [InlineData("Wtr/slry Ratio", 62)]         // symbols in key (idx 29 → 4+58)
    [InlineData("rho12_hat", 42)]              // idx 19 → 4+38
    [InlineData("rho12hat", 42)]               // underscore stripped
    [InlineData("DS Pump Press", 18)]          // wireName
    [InlineData("DSPumpPress", 18)]            // wireName normalized
    [InlineData("pumppressure", 16)]
    public void TryGetComAddress_NormalizedFallback(string name, int expectedAddress)
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetComAddress(name, out address), name);
        Assert.Equal(expectedAddress, address);
    }

    [Theory]
    // LAN-side normalization fallback (same law).
    [InlineData("Pump A Pressure", 266)]
    [InlineData("PumpAPressure", 266)]
    [InlineData("  pump A  pressure ", 266)]
    [InlineData("Cmnt Ops Track IDNum", 388)]
    [InlineData("CmntOpsTrackIDNum", 388)]
    public void TryGetLanAddress_NormalizedFallback(string name, int expectedAddress)
    {
        int address;
        Assert.True(DataMonitorAddressMap.TryGetLanAddress(name, out address), name);
        Assert.Equal(expectedAddress, address);
    }

    [Theory]
    // Normalization is NOT a fuzzy matcher and does NOT invent registry entries.
    [InlineData("Measured Depth")]
    [InlineData("Pumping Pressure")]
    [InlineData("Wellhead Pressure")]
    [InlineData("Combined Slurry Rate")]
    [InlineData("Fluid Density")]
    [InlineData("Hookload")]
    public void Lookups_NormalizationDoesNotInventEntries(string name)
    {
        int address;
        Assert.False(DataMonitorAddressMap.TryGetComAddress(name, out address));
        Assert.False(DataMonitorAddressMap.TryGetLanAddress(name, out address));
    }

    [Fact]
    public void Lookups_NullAndEmpty_ReturnFalse_NoThrow()
    {
        int address;
        Assert.False(DataMonitorAddressMap.TryGetComAddress(null, out address));
        Assert.False(DataMonitorAddressMap.TryGetComAddress("", out address));
        Assert.False(DataMonitorAddressMap.TryGetComAddress("   ", out address));
        Assert.False(DataMonitorAddressMap.TryGetLanAddress(null, out address));
        Assert.False(DataMonitorAddressMap.TryGetLanAddress("", out address));
        Assert.False(DataMonitorAddressMap.TryGetLanAddress("   ", out address));
    }

    [Fact]
    public void Normalization_RegistryCoverage_AllKeysResolveToOwnAddress()
    {
        // Every registry key must resolve to ITS OWN address through the public
        // lookup (exact fast path or normalized fallback).
        foreach (var pair in DataMonitorAddressMap.DebugComEntries())
        {
            int address;
            Assert.True(DataMonitorAddressMap.TryGetComAddress(pair.Key, out address), pair.Key);
            Assert.Equal(pair.Value, address);
            Assert.True(pair.Value >= BusLayout.ComStreamBaseReg, "entry '" + pair.Key + "' leaks below com_stream at " + pair.Value);
        }
        foreach (var pair in DataMonitorAddressMap.DebugLanEntries())
        {
            int address;
            Assert.True(DataMonitorAddressMap.TryGetLanAddress(pair.Key, out address), pair.Key);
            Assert.Equal(pair.Value, address);
            Assert.True(pair.Value >= BusLayout.LanStreamCrioBaseReg, "entry '" + pair.Key + "' leaks below lan_stream_crio at " + pair.Value);
        }
        Assert.Empty(DataMonitorAddressMap.DebugComAmbiguous()); // no normalized-key collisions
        Assert.Empty(DataMonitorAddressMap.DebugLanAmbiguous());
        Assert.True(DataMonitorAddressMap.DebugComNormCount() > 0);
        Assert.True(DataMonitorAddressMap.DebugLanNormCount() > 0);
    }

    [Fact]
    public void Lookups_UnknownName_ReturnsFalse_AndZeroAddress()
    {
        int address = -1;
        Assert.False(DataMonitorAddressMap.TryGetComAddress("NoSuchVariable", out address));
        Assert.Equal(0, address);
        address = -1;
        Assert.False(DataMonitorAddressMap.TryGetLanAddress("NoSuchVariable", out address));
        Assert.Equal(0, address);
    }
}
