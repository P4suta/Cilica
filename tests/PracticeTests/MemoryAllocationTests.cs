namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class MemoryAllocationTests
{
    [Fact]
    public void StackAllocatedBuffersCanFollowOtherCallArguments()
    {
        Assert.Equal(42, ConsumeStackBuffer(34, stackalloc byte[8]));
    }

    [DoNotOptimize]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ConsumeStackBuffer(int prefix, Span<byte> buffer)
    {
        buffer.Fill(1);
        return prefix + buffer.Length;
    }

    [Fact]
    public void MultidimensionalArrayConstructorsRemainIntact()
    {
        var values = new double[2, 3];
        values[1, 2] = 42;
        Assert.Equal(42, values[1, 2]);
        Assert.Equal(6, values.Length);
    }
}
