using System.Collections;
using System.Text.Json;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

public sealed class BusSnapshotOwnershipTests
{
    private static BusSnapshot Make(IReadOnlyList<ushort> words) => new(
        BusLayout.Magic, BusLayout.Version, BusLayout.LayoutVersion, (ushort)words.Count,
        words, new(ZoneKind.ComMain, 123456, true, 7), new(ZoneKind.Lan, 234567, false, 8),
        new(ZoneKind.RawReserve, 345678, true, 9), new(ZoneKind.Wits, 0, false, 0), false, 987654);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Constructor_OwnsArrayListAndReadOnlyWrapper(int kind)
    {
        ushort[] array = [0x0014, 0x00A7, 3, 4];
        var list = array.ToList();
        IReadOnlyList<ushort> source = kind switch { 0 => array, 1 => list, _ => Array.AsReadOnly(array) };
        var snapshot = Make(source);
        int hash = snapshot.GetHashCode();
        array[0] = 99;
        list[0] = 98;
        Assert.Equal((ushort)0x0014, snapshot.Words[0]);
        Assert.Equal(hash, snapshot.GetHashCode());
        Assert.NotSame(source, snapshot.Words);
    }

    private sealed class CallerCollection : List<ushort>, ICollection<ushort>
    {
        public ushort[]? RetainedCopyTarget { get; private set; }
        void ICollection<ushort>.CopyTo(ushort[] array, int arrayIndex)
        {
            RetainedCopyTarget = array;
            base.CopyTo(array, arrayIndex);
            // A caller-controlled CopyTo could retain and later corrupt this array.
        }
    }

    [Fact]
    public void CallerCollection_CannotRetainAndMutateSnapshotBacking()
    {
        var source = new CallerCollection { 0x0014, 0x00A7 };
        var snapshot = Make(source);
        source[0] = 99;
        if (source.RetainedCopyTarget is { } retained) retained[0] = 98;
        Assert.Equal((ushort)0x0014, snapshot.Words[0]);
    }

    [Fact]
    public void InitAndWith_OwnReplacements_StaleCopySharesOnlyImmutableOwner()
    {
        ushort[] source = [1, 2, 3, 4];
        var original = Make(source);
        ushort[] replacement = [0x0014, 0x00A7, 6, 7];
        var initialized = new BusSnapshot(1, 2, 3, 4, source, default, default, default, default, false, 5)
            { Words = replacement };
        var changed = original with { Words = replacement };
        var stale = original with { Stale = true };
        var copy = original with { };
        replacement[0] = source[0] = 99;
        Assert.Equal((ushort)0x0014, initialized.Words[0]);
        Assert.Equal((ushort)0x0014, changed.Words[0]);
        Assert.NotSame(changed.Words, initialized.Words);
        Assert.Same(original.Words, stale.Words);
        Assert.True(stale.Stale);
        Assert.Equal(original, stale with { Stale = false });
        Assert.Equal(original, copy);
        Assert.Equal(original.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(original, Make([1, 2, 3, 4])); // owner identity, not structural equality
        var (_, _, _, _, words, _, _, _, _, _, _) = original;
        Assert.Same(original.Words, words);
        Assert.Equal((ushort)1, words[0]);
    }

    [Fact]
    public void NullWords_AreRejectedByConstructorAndInit()
    {
        Assert.Throws<ArgumentNullException>(() => new BusSnapshot(1, 2, 3, 2,
            null!, default, default, default, default, false, 5));
        var original = Make([1, 2]);
        Assert.Throws<ArgumentNullException>(() => original with { Words = null! });
        Assert.Throws<ArgumentNullException>(() => new BusSnapshot(1, 2, 3, 2,
            new ushort[] { 1, 2 }, default, default, default, default, false, 5) { Words = null! });
        Assert.Equal(new ushort[] { 1, 2 }, original.Words.ToArray());
    }

    [Fact]
    public void PublishedCollections_RejectGenericAndNonGenericMutation()
    {
        var snapshot = Make([1, 2, 3, 4]);
        Assert.False(snapshot.Words is ushort[]);
        if (snapshot.Words is IList<ushort> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = 99);
            Assert.Throws<NotSupportedException>(() => generic.Add(99));
        }
        if (snapshot.Words is IList nongeneric)
        {
            Assert.True(nongeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = (ushort)99);
            Assert.Throws<NotSupportedException>(() => nongeneric.Clear());
        }
        // Array.AsReadOnly used to permit this entirely safe backing recovery.
        if (snapshot.Words is ICollection collection && collection.SyncRoot is ushort[] backing)
            backing[0] = 99;
        Assert.Equal((ushort)1, snapshot.Words[0]);
    }

    [Fact]
    public void CopyWordsTo_ValidatesWholeRangeBeforeAnyWrite_AndCopiesValues()
    {
        var snapshot = Make([1, 2, 3, 4]);
        ushort[] target = [77, 88, 99];
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.CopyWordsTo(-1, target));
        Assert.Equal(new ushort[] { 77, 88, 99 }, target);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.CopyWordsTo(5, target));
        Assert.Equal(new ushort[] { 77, 88, 99 }, target);
        Assert.Throws<ArgumentException>(() => snapshot.CopyWordsTo(2, target));
        Assert.Equal(new ushort[] { 77, 88, 99 }, target);
        snapshot.CopyWordsTo(1, target);
        Assert.Equal(new ushort[] { 2, 3, 4 }, target);
        target[0] = 55;
        Assert.Equal((ushort)2, snapshot.Words[1]);
        Assert.Throws<IndexOutOfRangeException>(() => snapshot.GetRegister(4));
        snapshot.CopyWordsTo(4, Span<ushort>.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.CopyWordsTo(int.MaxValue, Span<ushort>.Empty));
    }

    [Theory]
    [InlineData(0x7FC12345u)] // payload qNaN
    [InlineData(0xFFC12345u)] // negative payload qNaN
    [InlineData(0x7F812345u)] // signalling pattern: copy never performs float arithmetic
    [InlineData(0x80000000u)] // negative zero
    [InlineData(0x00000001u)] // subnormal
    [InlineData(0x00A70014u)] // distinctive raw words
    public void RawBitsAndDerivedCopies_AreIndependent(uint bits)
    {
        ushort[] source = [(ushort)bits, (ushort)(bits >> 16)];
        var snapshot = Make(source);
        source[0] = source[1] = 0;
        ushort[] copied = new ushort[2];
        snapshot.CopyWordsTo(0, copied);
        Assert.Equal(bits, (uint)copied[0] | ((uint)copied[1] << 16));
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(snapshot.GetZoneFloats(0, 2)[0]));
        copied[0] = 99;
        var exposedCopy = snapshot.Words.ToArray();
        exposedCopy[0] = 98;
        var floats = snapshot.GetZoneFloats(0, 2);
        floats[0] = 0;
        Assert.Equal(bits, (uint)snapshot.Words[0] | ((uint)snapshot.Words[1] << 16));
    }

    [Fact]
    public void JsonRoundTrip_PreservesArraySchemaMetadataAndRawWords()
    {
        var original = Make([0x0014, 0x00A7, 0x2345, 0x7FC1, 0, 0x8000]) with { Stale = true };
        string json = JsonSerializer.Serialize(original);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("Words").ValueKind);
        var restored = Assert.IsType<BusSnapshot>(JsonSerializer.Deserialize<BusSnapshot>(json));
        Assert.Equal(original.Words.ToArray(), restored.Words.ToArray());
        Assert.Equal(original.Magic, restored.Magic);
        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.LayoutVersion, restored.LayoutVersion);
        Assert.Equal(original.RegCount, restored.RegCount);
        Assert.Equal(original.ComMain, restored.ComMain);
        Assert.Equal(original.Lan, restored.Lan);
        Assert.Equal(original.RawReserve, restored.RawReserve);
        Assert.Equal(original.Wits, restored.Wits);
        Assert.Equal(original.Stale, restored.Stale);
        Assert.Equal(original.CapturedAtTickMs, restored.CapturedAtTickMs);
        if (restored.Words is IList<ushort> mutable)
            Assert.Throws<NotSupportedException>(() => mutable[0] = 99);
        Assert.Equal((ushort)0x0014, restored.Words[0]);
    }
}
