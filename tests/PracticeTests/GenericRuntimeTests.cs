namespace DistIL.PracticeTests;

using System.Numerics;
using System.Runtime.CompilerServices;
using DistIL.Attributes;

[Optimize]
public class GenericRuntimeTests
{
    [Fact]
    public void StaticInterfaceCallsDoNotHaveAnInstanceReceiver()
    {
        Assert.Equal(42, Sum<int>(20, 22));
        Assert.Equal(42L, Sum<long>(20, 22));
        Assert.Equal(4.5, Sum<double>(2, 2.5));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static T Sum<T>(T a, T b) where T : INumber<T> => T.Zero + a + b;

    [Fact]
    public void NullableBoxingUsesTheUnderlyingStructsInterfaces()
    {
        Assert.Null(BoxNullable<int>(null));
        Assert.Equal(0, BoxNullable<int>(42)!.CompareTo(42));
        Assert.Equal(0, BoxNullable<decimal>(42m)!.CompareTo(42m));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static IComparable? BoxNullable<T>(T? value) where T : struct, IComparable => value;
}
