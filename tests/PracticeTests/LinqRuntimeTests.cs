namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class LinqRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void MaterializedArraysShareEmptyStorage(int count)
    {
        var source = Enumerable.Range(1, count).ToArray();
        var first = source.Select(value => value * 2).ToArray();
        var second = source.Select(value => value * 2).ToArray();
        Assert.Equal(source.Select(value => value * 2), first);
        if (count == 0) {
            Assert.Same(Array.Empty<int>(), first);
            Assert.Same(first, second);
            Assert.Same(first, source.Where(value => value > 0).ToArray());
            Assert.Same(first, Enumerable.Range(1, 3).Take(count).ToArray());
        } else {
            Assert.NotSame(first, second);
        }
    }

    [Fact]
    public void GenericEnumerableSourcesSupportReferenceAndValueTypes()
    {
        Assert.Equal(new[] { 1 }, TakeFirst<List<int>, int>([1, 2]));
        Assert.Equal(new[] { "first" }, TakeFirst<string[], string>(["first", "second"]));
        Assert.Equal(new[] { 3 }, TakeFirst<ArraySegment<int>, int>(new(new[] { 3, 4 })));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static T[] TakeFirst<TCollection, T>(TCollection source) where TCollection : IEnumerable<T>
        => source.Take(1).ToArray();

    [Fact]
    public void NullableEnumerableSourcesUseTheirBoxedUnderlyingValue()
    {
        Assert.Equal(new[] { 7 }, TakeFirstNullable(new ArraySegment<int>(new[] { 7, 8 })));
        Assert.Throws<ArgumentNullException>(() => TakeFirstNullable(null));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int[] TakeFirstNullable(ArraySegment<int>? source)
    {
        IEnumerable<int>? sequence = source;
        return sequence!.Take(1).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void AggregateUsesItsDeclaredAccumulatorType(int count)
    {
        int[] values = Enumerable.Range(1, count).ToArray();
        IEnumerable<int> seed = new List<int> { 9 };
        var result = values.Aggregate(seed, (accumulator, value) => accumulator.Append(value));
        int total = values.Aggregate(seed, (accumulator, value) => accumulator.Append(value), accumulator => accumulator.Sum());
        string? text = values.Aggregate<int, string?>(null, (accumulator, value) => (accumulator ?? "") + "x");

        Assert.Equal(count + 1, result.Count());
        Assert.Equal(9, result.First());
        Assert.Equal(9 + count * (count + 1) / 2, total);
        Assert.Equal(count == 0 ? null : new string('x', count), text);
    }

    [Fact]
    public void SeedlessAggregateAcceptsCovariantSources()
    {
        string[] values = ["Alpha", "Beta"];
        object result = values.Aggregate<object>((accumulator, value) => value.ToString()!.Length);

        Assert.Equal(4, result);
    }

    [Fact]
    public void FlattenedAggregatesPreserveAccumulatorTypesAcrossSkippedItems()
    {
        int[][] chunks = [[1, 2], [], [3]];
        var result = chunks.SelectMany(chunk => chunk).Where(value => value != 2)
            .Aggregate((IEnumerable<int>)new List<int> { 9 }, (accumulator, value) => accumulator.Append(value));

        Assert.Equal(new[] { 9, 1, 3 }, result);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ToDictionaryValidatesNullSelectorsBeforeEnumeration(int count)
    {
        int[] source = Enumerable.Range(0, count).ToArray();

        Assert.Equal("keySelector", Assert.Throws<ArgumentNullException>(
            () => source.ToDictionary((Func<int, int>)null!)).ParamName);
        Assert.Equal("elementSelector", Assert.Throws<ArgumentNullException>(
            () => source.ToDictionary(value => value, (Func<int, int>)null!)).ParamName);
        Assert.Equal("keySelector", Assert.Throws<ArgumentNullException>(
            () => source.ToDictionary((Func<int, int>)null!, (Func<int, int>)null!)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void QueriesValidateNullDelegatesBeforeEnumeration(int count)
    {
        int[] source = Enumerable.Range(0, count).ToArray();

        Assert.Equal("selector", Assert.Throws<ArgumentNullException>(
            () => source.Select((Func<int, int, int>)null!).ToArray()).ParamName);
        Assert.Equal("predicate", Assert.Throws<ArgumentNullException>(
            () => source.Where((Func<int, bool>)null!).ToArray()).ParamName);
        Assert.Equal("func", Assert.Throws<ArgumentNullException>(
            () => source.Aggregate(0, (Func<int, int, int>)null!)).ParamName);
        Assert.Equal("resultSelector", Assert.Throws<ArgumentNullException>(
            () => source.Aggregate(0, (sum, value) => sum + value, (Func<int, int>)null!)).ParamName);
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
