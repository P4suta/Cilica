namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class OptimizerRegressionTests
{
    [Theory]
    [InlineData("-", "0")]
    [InlineData("OFF", "0")]
    [InlineData("Off", "0")]
    [InlineData("on", "on")]
    public void SharedSelectLocalsPreserveBothPaths(string value, string expected)
    {
        if (value == "-" || value.ToUpperInvariant() == "OFF") value = "0";
        Assert.Equal(expected, value);
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
    public void FilterCallsRetainTheirOwnExceptionRegions()
    {
        var trace = new List<int>();
        try {
            throw new InvalidOperationException();
        } catch (InvalidOperationException) when (FilterWithFinally(trace)) {
            trace.Add(2);
        }
        Assert.Equal(new[] { 1, 2 }, trace);
    }

    static bool FilterWithFinally(List<int> trace)
    {
        try { return trace.Count == 0; }
        finally { trace.Add(1); }
    }

    [Fact]
    public void NestedFinallyInAHandlerPrecedesItsEnclosingClause()
    {
        var trace = new List<int>();
        try {
            throw new InvalidOperationException();
        } catch (InvalidOperationException) {
            try { trace.Add(1); }
            finally { trace.Add(2); }
        }
        Assert.Equal(new[] { 1, 2 }, trace);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NestedFinallyUnwindsInOrder(int mode)
    {
        var order = new List<int>();
        if (mode < 0) {
            Assert.Throws<InvalidOperationException>(() => LeaveNestedRegions(mode, order));
        } else {
            Assert.Equal(mode < 2 ? mode + 10 : 20, LeaveNestedRegions(mode, order));
        }
        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int LeaveNestedRegions(int mode, List<int> order)
    {
        try {
            try {
                try {
                    if (mode < 0) throw new InvalidOperationException();
                    if (mode == 0) return 10;
                    if (mode == 1) return 11;
                } finally { order.Add(1); }
            } finally { order.Add(2); }
        } finally { order.Add(3); }
        return 20;
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedLockTemporariesDoNotEscapeCatchRegions(bool fail)
    {
        var trace = new List<int>();
        try {
            try {
                lock (trace) {
                    if (fail) throw new InvalidOperationException();
                }
                lock (trace) { trace.Add(1); }
            } catch (InvalidOperationException) { trace.Add(2); }
        } finally { trace.Add(3); }
        Assert.Equal(fail ? new[] { 2, 3 } : new[] { 1, 3 }, trace);
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
