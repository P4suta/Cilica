namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class LinqRuntimeTests
{
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
