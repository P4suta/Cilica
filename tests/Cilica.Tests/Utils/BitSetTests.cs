namespace Cilica.Tests.Util;

using Cilica.Util;

public class BitSetTests
{
    static readonly int[] s_Capacities = [0, 64, 65, 256, 257];

    public static IEnumerable<object[]> CapacityPairs()
    {
        foreach (int a in s_Capacities) {
            foreach (int b in s_Capacities) {
                yield return [a, b];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CapacityPairs))]
    public void IntersectMatchesHashSet(int capacity, int otherCapacity)
    {
        var set = CreateSet(capacity, 0);
        var other = CreateSet(otherCapacity, 1);
        var original = ReadSet(set);
        var expected = new HashSet<int>(original);
        expected.IntersectWith(ReadSet(other));
        var storage = set.RawData;
        var otherWords = other.RawData.ToArray();

        Assert.Equal(!original.SetEquals(expected), set.Intersect(other));
        AssertSetEqual(expected, set);
        Assert.Same(storage, set.RawData);
        Assert.Equal(otherWords, other.RawData);
        Assert.False(set.Intersect(other));
        AssertSetEqual(expected, set);

        Assert.False(set.Intersect(set));
        AssertSetEqual(expected, set);
        Assert.Equal(expected.Count != 0, set.Intersect(new BitSet(otherCapacity)));
        Assert.Equal(0, set.PopCount());
        Assert.Same(storage, set.RawData);
    }

    [Fact]
    public void IntersectReportsChangesOnlyInTrailingWords()
    {
        var set = new BitSet(257);
        set.Add(256);

        Assert.True(set.Intersect(new BitSet(64)));
        Assert.Equal(0, set.PopCount());
        Assert.False(set.Intersect(new BitSet(64)));
    }

    [Theory]
    [MemberData(nameof(CapacityPairs))]
    public void UnionDiffsMatchesHashSet(int inputCapacity, int exclusionCapacity)
    {
        foreach (int capacity in s_Capacities) {
            VerifyUnionDiffs(CreateSet(capacity, 2), CreateSet(inputCapacity, 0), CreateSet(exclusionCapacity, 1));
            VerifyUnionDiffs(new BitSet(capacity), new BitSet(inputCapacity), new BitSet(exclusionCapacity));
        }
    }

    [Theory]
    [MemberData(nameof(CapacityPairs))]
    public void UnionDiffsSupportsAliasedOperands(int capacity, int otherCapacity)
    {
        var input = CreateSet(capacity, 0);
        VerifyUnionDiffs(input, input, CreateSet(otherCapacity, 1));

        var excluded = CreateSet(capacity, 1);
        VerifyUnionDiffs(excluded, CreateSet(otherCapacity, 0), excluded);

        var sameInput = CreateSet(otherCapacity, 0);
        VerifyUnionDiffs(CreateSet(capacity, 2), sameInput, sameInput);
        VerifyUnionDiffs(sameInput, sameInput, sameInput);
    }

    static void VerifyUnionDiffs(BitSet destination, BitSet input, BitSet excluded)
    {
        var original = ReadSet(destination);
        var expected = ReadSet(input);
        expected.ExceptWith(ReadSet(excluded));
        expected.UnionWith(original);
        var inputWords = input.RawData.ToArray();
        var excludedWords = excluded.RawData.ToArray();
        int expectedCapacity = Math.Max(destination.RawData.Length, input.RawData.Length);

        Assert.Equal(!original.SetEquals(expected), destination.UnionDiffs(input, excluded));
        AssertSetEqual(expected, destination);
        Assert.Equal(expectedCapacity, destination.RawData.Length);
        if (input != destination) Assert.Equal(inputWords, input.RawData);
        if (excluded != destination) Assert.Equal(excludedWords, excluded.RawData);
        Assert.False(destination.UnionDiffs(input, excluded));
        AssertSetEqual(expected, destination);
    }

    static BitSet CreateSet(int capacity, int offset)
    {
        var set = new BitSet(capacity);
        int[] positions = [0, 1, 62, 63, 64, 65, 126, 127, 128, 254, 255, 256];
        for (int i = 0; i < positions.Length; i++) {
            if (positions[i] < capacity && (i + offset) % 3 != 0) set.Add(positions[i]);
        }
        return set;
    }

    static HashSet<int> ReadSet(BitSet set)
        => Enumerable.Range(0, set.RawData.Length * 64).Where(set.Contains).ToHashSet();

    static void AssertSetEqual(HashSet<int> expected, BitSet actual)
    {
        Assert.Equal(expected.Count, actual.PopCount());
        foreach (int value in expected) Assert.True(actual.Contains(value));
    }

    [Fact]
    public void Test_Everything()
    {
        var bs = new BitSet(130);
        int[] positions = [1, 3, 4, 8, 12, 17, 32, 55, 63, 64, 65, 100, 126];
        foreach (int pos in positions) {
            Assert.False(bs.Contains(pos));
            Assert.True(bs.Add(pos));
            Assert.True(bs.Contains(pos));
            Assert.False(bs.Add(pos));
        }
        Assert.Equal(positions.Length, bs.PopCount());

        int i = 0;
        foreach (int index in bs) {
            Assert.Equal(positions[i++], index);
        }

        Assert.True(bs.ContainsRange(0, 4));
        Assert.True(bs.ContainsRange(33, 56));
        Assert.True(bs.ContainsRange(100, 127));
        Assert.False(bs.ContainsRange(18, 32));
        Assert.False(bs.ContainsRange(33, 55));
        Assert.False(bs.ContainsRange(101, 126));

        bs.Clear();
        Assert.Equal(0, bs.PopCount());

        Assert.False(bs[222]);
        bs[222] = true;
        Assert.True(bs[222]);

        Assert.ThrowsAny<Exception>(() => bs[-1] = true);
    }
}
