namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class ArrayRuntimeTests
{
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
