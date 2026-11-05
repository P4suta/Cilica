namespace Cilica.PracticeTests;

using System.Numerics;
using System.Runtime.CompilerServices;
using Cilica.Attributes;

[Optimize]
public class GenericRuntimeTests
{
    [Theory, CheckCodeGenAfterRun]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(257)]
    public void GenericDefaultSeedsVectorizedReduction(int count)
    {
        var a = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var b = Enumerable.Repeat(3, count).ToArray();
        int result = Aggregate(a, b, (x, y) => x * y, (x, y) => x + y);
        Assert.Equal(unchecked(int.MaxValue * 3 * count), result);
        // CHECK: Vector256
        // CHECK-NOT: CIL::MemSet
        // CHECK-NOT: ::Invoke
    }

    [Theory, CheckCodeGenAfterRun]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(17)]
    public void GenericDefaultSeedsWideVectorizedReduction(int count)
    {
        var a = Enumerable.Repeat(long.MaxValue, count).ToArray();
        var b = Enumerable.Repeat(3L, count).ToArray();
        long result = Aggregate(a, b, (x, y) => x * y, (x, y) => x + y);
        Assert.Equal(unchecked(long.MaxValue * 3 * count), result);
        // CHECK: Vector256
        // CHECK-NOT: CIL::MemSet
        // CHECK-NOT: ::Invoke
    }

    [Fact, CheckCodeGenAfterRun]
    public void GenericFloatingReductionPreservesEvaluationOrder()
    {
        double[] a = [1e20, 1, -1e20, 1, 1e20, 1, -1e20, 1, 1e20, 1, -1e20, 1, 1e20, 1, -1e20, 1];
        var b = Enumerable.Repeat(1.0, a.Length).ToArray();
        Assert.Equal(1.0, Aggregate(a, b, (x, y) => x * y, (x, y) => x + y));
        // CHECK-NOT: Vector256
        // CHECK-NOT: ::Invoke
        // CHECK-NOT: ::Aggregate
    }

    [Fact]
    public void GenericAggregationPreservesLengthAndNullChecks()
    {
        Assert.Throws<ArgumentException>(() => Aggregate(new int[17], new int[16], (x, y) => x * y, (x, y) => x + y));
        Assert.Throws<NullReferenceException>(() => Aggregate<int>(null!, [], (x, y) => x * y, (x, y) => x + y));
        Assert.Throws<NullReferenceException>(() => Aggregate<int>([], null!, (x, y) => x * y, (x, y) => x + y));
    }

    static T Aggregate<T>(T[] a, T[] b, Func<T, T, T> selector, Func<T, T, T> aggregator) where T : struct
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Arrays must have the same length.");

        T result = default;
        for (int i = 0; i < a.Length; i++)
            result = aggregator(result, selector(a[i], b[i]));
        return result;
    }

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

    [Fact]
    public void ConcreteNullablePrimitivesCanBePassedAsInterfaces()
    {
        int? number = 42;
        char? character = 'A';
        bool? flag = true;
        Assert.Equal(0, CompareBoxed(number, 42));
        Assert.Equal(0, CompareBoxed(character, 'A'));
        Assert.Equal(0, CompareBoxed(flag, true));
        number = null;
        Assert.Null(CompareBoxed(number, 42));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int? CompareBoxed(IComparable? value, object other) => value?.CompareTo(other);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static IComparable? BoxNullable<T>(T? value) where T : struct, IComparable => value;
}
