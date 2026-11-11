using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Sdk.Tests;

// W1 contract tests: register map v2 addressing rule (zoneBase + index × 2) and overflow rule.
public class RegisterMapV2Tests
{
    [Fact]
    public void WordsPerFloat_Is_Two()
    {
        Assert.Equal(2, RegisterMapV2.WordsPerFloat);
    }

    [Fact]
    public void AddressFor_ComMain_Starts_At_4_Steps_By_2()
    {
        Assert.Equal(4, RegisterMapV2.AddressFor(ZoneKind.ComMain, 0));
        Assert.Equal(6, RegisterMapV2.AddressFor(ZoneKind.ComMain, 1));
        Assert.Equal(8, RegisterMapV2.AddressFor(ZoneKind.ComMain, 2));
        // Last slot: 4 + 97 × 2 = 198 → pair 198/199.
        Assert.Equal(198, RegisterMapV2.AddressFor(ZoneKind.ComMain, 97));
    }

    [Fact]
    public void AddressFor_Lan_Starts_At_200()
    {
        Assert.Equal(200, RegisterMapV2.AddressFor(ZoneKind.Lan, 0));
        Assert.Equal(202, RegisterMapV2.AddressFor(ZoneKind.Lan, 1));
        Assert.Equal(448, RegisterMapV2.AddressFor(ZoneKind.Lan, 124));
    }

    [Fact]
    public void AddressFor_RawReserve_Starts_At_450()
    {
        Assert.Equal(450, RegisterMapV2.AddressFor(ZoneKind.RawReserve, 0));
        Assert.Equal(452, RegisterMapV2.AddressFor(ZoneKind.RawReserve, 1));
        Assert.Equal(598, RegisterMapV2.AddressFor(ZoneKind.RawReserve, 74));
    }

    [Fact]
    public void AddressFor_Wits_Starts_At_600()
    {
        Assert.Equal(600, RegisterMapV2.AddressFor(ZoneKind.Wits, 0));
        Assert.Equal(602, RegisterMapV2.AddressFor(ZoneKind.Wits, 1));
        Assert.Equal(604, RegisterMapV2.AddressFor(ZoneKind.Wits, 2));
        // Last slot: 600 + 24 × 2 = 648 → pair 648/649.
        Assert.Equal(648, RegisterMapV2.AddressFor(ZoneKind.Wits, 24));
    }

    [Fact]
    public void AddressFor_LanExt_Starts_At_650()
    {
        Assert.Equal(650, RegisterMapV2.AddressFor(ZoneKind.LanExt, 0));
        Assert.Equal(652, RegisterMapV2.AddressFor(ZoneKind.LanExt, 1));
        Assert.Equal(654, RegisterMapV2.AddressFor(ZoneKind.LanExt, 2));
        // Last slot: 650 + 3876 × 2 = 8402 → pair 8402/8403 (full dictionary end).
        Assert.Equal(8402, RegisterMapV2.AddressFor(ZoneKind.LanExt, 3876));
    }

    [Fact]
    public void AddressFor_Descriptor_Overload_Matches_Default_Overload()
    {
        Assert.Equal(RegisterMapV2.AddressFor(ZoneKind.ComMain, 10),
                     RegisterMapV2.AddressFor(ZoneDescriptor.ComMain, 10));
        Assert.Equal(RegisterMapV2.AddressFor(ZoneKind.Lan, 10),
                     RegisterMapV2.AddressFor(ZoneDescriptor.Lan, 10));
        Assert.Equal(RegisterMapV2.AddressFor(ZoneKind.Wits, 10),
                     RegisterMapV2.AddressFor(ZoneDescriptor.Wits, 10));
        Assert.Equal(RegisterMapV2.AddressFor(ZoneKind.LanExt, 10),
                     RegisterMapV2.AddressFor(ZoneDescriptor.LanExt, 10));
    }

    [Fact]
    public void Zone_Capacity_Boundary_Is_Exact()
    {
        // index < FloatCapacity ⇒ fits; index >= FloatCapacity ⇒ overflow (truncate + warn).
        Assert.False(RegisterMapV2.IsOverflow(ZoneDescriptor.ComMain, 97));
        Assert.True(RegisterMapV2.IsOverflow(ZoneDescriptor.ComMain, 98));

        Assert.False(RegisterMapV2.IsOverflow(ZoneDescriptor.Lan, 124));
        Assert.True(RegisterMapV2.IsOverflow(ZoneDescriptor.Lan, 125));

        Assert.False(RegisterMapV2.IsOverflow(ZoneDescriptor.RawReserve, 74));
        Assert.True(RegisterMapV2.IsOverflow(ZoneDescriptor.RawReserve, 75));

        Assert.False(RegisterMapV2.IsOverflow(ZoneDescriptor.Wits, 24));
        Assert.True(RegisterMapV2.IsOverflow(ZoneDescriptor.Wits, 25));

        Assert.False(RegisterMapV2.IsOverflow(ZoneDescriptor.LanExt, 3876));
        Assert.True(RegisterMapV2.IsOverflow(ZoneDescriptor.LanExt, 3877));
    }

    [Fact]
    public void Entry_Record_Is_Immutable_Value_Shape()
    {
        var entry = new RegisterEntryV2("Lan/RecircDensity", ZoneKind.Lan, 200);
        Assert.Equal("Lan/RecircDensity", entry.CanonicalName);
        Assert.Equal(ZoneKind.Lan, entry.Zone);
        Assert.Equal(200, entry.Address);

        var copy = entry; // value copy
        copy = new RegisterEntryV2("X", ZoneKind.RawReserve, 450);
        Assert.Equal("Lan/RecircDensity", entry.CanonicalName); // original untouched
        Assert.Equal(200, entry.Address);
    }
}
