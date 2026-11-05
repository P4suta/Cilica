namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class LinqRuntimeTests
{
    [Fact]
    public void ToDictionaryDistinguishesComparersFromElementSelectors()
    {
        string[] source = ["Alpha", "Beta"];
        var comparer = StringComparer.OrdinalIgnoreCase;
        var values = source.ToDictionary(value => value, comparer);
        var lengths = source.ToDictionary(value => value, value => value.Length, comparer);
        var defaultComparer = source.ToDictionary(value => value, (IEqualityComparer<string>?)null);

        Assert.Same(comparer, values.Comparer);
        Assert.Equal("Alpha", values["ALPHA"]);
        Assert.Equal(4, lengths["BETA"]);
        Assert.Equal("Alpha", defaultComparer["Alpha"]);
        Assert.False(defaultComparer.ContainsKey("ALPHA"));
    }

    [Fact]
    public void ToDictionaryAcceptsKeyValuePairsAndTuples()
    {
        KeyValuePair<string, int>[] pairs = [new("Alpha", 1), new("Beta", 2)];
        (string, int)[] tuples = [("Alpha", 1), ("Beta", 2)];
        var pairValues = pairs.ToDictionary();
        var tupleValues = tuples.ToDictionary();
        var pairsWithComparer = pairs.ToDictionary(StringComparer.OrdinalIgnoreCase);
        var tuplesWithComparer = tuples.ToDictionary(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1, pairValues["Alpha"]);
        Assert.Equal(2, tupleValues["Beta"]);
        Assert.Equal(1, pairsWithComparer["ALPHA"]);
        Assert.Equal(2, tuplesWithComparer["BETA"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void SharedQueriesRetainEveryConsumer(int count)
    {
        int visits = 0;
        var query = Enumerable.Range(0, count)
            .Select(value => { visits++; return value * 2; })
            .Where(value => value % 4 == 0);

        Assert.Equal((count + 1) / 2, MaterializeSharedQuery(query).Length);
        Assert.True(query.All(value => value % 4 == 0));
        Assert.Equal(0, query.SingleOrDefault(value => value == 0));
        Assert.True(query.All(value => value >= 0));
        Assert.Equal(count * 4, visits);
    }

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    static int[] MaterializeSharedQuery(IEnumerable<int> source) => source.ToArray();
}
