namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class ArrayRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void StoredInductionValuesRemainSequential(int count)
    {
        Assert.Equal(Enumerable.Range(1, count), CreateSequence(count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int[] CreateSequence(int count)
    {
        var values = new int[count];
        for (int i = 0; i < values.Length; i++) values[i] = i + 1;
        return values;
    }

    [Fact]
    public void CovariantArrayReadsRemainValidAcrossMutation()
    {
        object[] values = new string[] { "first" };
        var result = ReadAroundMutation(values, () => values[0] = "second");

        Assert.Equal("first", result.Before);
        Assert.Equal("second", result.After);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object Before, object After) ReadAroundMutation(object[] values, Action mutate)
    {
        var before = values[0];
        mutate();
        return (before, values[0]);
    }
}
