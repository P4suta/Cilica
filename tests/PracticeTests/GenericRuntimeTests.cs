namespace Cilica.PracticeTests;

using System.Numerics;
using System.Runtime.CompilerServices;
using Cilica.Attributes;

[Optimize]
public class GenericRuntimeTests
{
    [Fact]
    public void ConstrainedClassCallsReadTheReferencedInstance()
    {
        var node = new ConstrainedNode { Value = 42 };
        Assert.Equal(42, ReadNode(ref node));
        node = null!;
        Assert.Throws<NullReferenceException>(() => ReadNode(ref node));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ReadNode<T>(ref T node) where T : ConstrainedNode => node.Read();

    class ConstrainedNode
    {
        public int Value;
        public int Read() => Value;
    }

    [Fact]
    public void InlinedTypeTokensKeepGenericDefinitions()
    {
        Assert.Equal(typeof(Nullable<>), OpenGeneric<int>());
        Assert.Equal(typeof(List<>), OpenGenericList<string>());
    }

    static Type OpenGeneric<T>() => typeof(Nullable<>);
    static Type OpenGenericList<T>() => typeof(List<>);

    [Fact]
    public void BoxedGenericConstraintsRemainUsable()
    {
        var stream = new MemoryStream();
        DisposeGeneric(stream);
        Assert.False(stream.CanRead);
        var other = new MemoryStream();
        GenericHolder<MemoryStream>.Dispose(other);
        Assert.False(other.CanRead);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DisposeGeneric<T>(T value) where T : IDisposable => ((IDisposable)value).Dispose();

    [Optimize]
    static class GenericHolder<T> where T : Stream
    {
        public static void Dispose(T value) => ((Stream)value).Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedCollectionsKeepTheirCommonInterface(bool populated)
    {
        List<int>? values = populated ? new List<int> { 42 } : null;
        var result = MergeCollections(values);
        Assert.Equal(populated ? 1 : 0, result.Count);
        if (populated) Assert.Equal(42, result[0]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static IReadOnlyList<int> MergeCollections(List<int>? values) => values ?? (IReadOnlyList<int>)Array.Empty<int>();

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
