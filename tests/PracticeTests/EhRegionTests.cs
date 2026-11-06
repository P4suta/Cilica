namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class EhRegionTests
{
    [Fact]
    public void ThrowingCallsPreventLaterExceptionRegionsFromExecuting()
    {
        var error = new InvalidOperationException("stop");
        int cleanups = 0;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => ThrowBeforeFinally(error, () => cleanups++)));
        Assert.Equal(0, cleanups);
    }

    static int ThrowBeforeFinally(Exception error, Action cleanup)
    {
        ThrowException(true, error);
        return ReturnWithFinally(cleanup);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ThrowException(bool shouldThrow, Exception error)
    {
        if (shouldThrow) throw error;
    }

    static int ReturnWithFinally(Action cleanup)
    {
        try { cleanup(); return 42; }
        finally { cleanup(); }
    }

    [Fact]
    public void FinallyWritesSurviveAnEnclosingCatch()
    {
        var value = new Pair();
        try {
            try {
                throw new InvalidOperationException();
            } finally {
                value = CreatePair();
            }
        } catch (InvalidOperationException) { }
        Assert.Equal(100, value.X + value.Y);
    }

    struct Pair { public short X, Y; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Pair CreatePair() => new() { X = 75, Y = 25 };

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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void FinallyRegionsInBothTryAndHandlerUnwindInOrder(int failAt)
    {
        var trace = new List<int>();
        var exception = new InvalidOperationException();
        void Record(int value)
        {
            trace.Add(value);
            if (value == failAt) throw exception;
        }

        if (failAt == 0) {
            RunFinallyRegionsInBothTryAndHandler(Record);
        } else {
            Assert.Same(exception, Assert.Throws<InvalidOperationException>(
                () => RunFinallyRegionsInBothTryAndHandler(Record)));
        }
        int[] expected = failAt switch {
            1 => [1, 2, 6, 7, 8, 9, 10, 11],
            6 => [1, 2, 3, 4, 5, 6, 7, 8, 11],
            _ => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]
        };
        Assert.Equal(expected, trace);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunFinallyRegionsInBothTryAndHandler(Action<int> record)
    {
        try {
            try { record(1); }
            finally { record(2); }
            try {
                try { record(3); }
                finally { record(4); }
            } finally { record(5); }
        } finally {
            try {
                try {
                    try { record(6); }
                    finally { record(7); }
                } finally { record(8); }
                try { record(9); }
                finally { record(10); }
            } finally { record(11); }
        }
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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 42)]
    [InlineData(2, 42)]
    [CheckCodeGenAfterRun]
    public void ScalarReplacedFieldsPreserveExceptionState(int failurePoint, int expected)
    {
        var holder = new ValueHolder();
        try {
            holder.Value = ComputeValue(failurePoint);
            ThrowAfterStore(failurePoint);
        } catch (InvalidOperationException) {
        }
        Assert.Equal(expected, holder.Value);

        // CHECK: sroa.Value: int
        // CHECK: store $sroa.Value, 0
        // CHECK: try catch
        // CHECK: store $sroa.Value,
        // CHECK: load $sroa.Value
    }

    [Optimize]
    sealed class ValueHolder
    {
        public int Value;
    }

    [DoNotOptimize]
    static int ComputeValue(int failurePoint) => failurePoint == 0 ? throw new InvalidOperationException() : 42;

    [DoNotOptimize]
    static void ThrowAfterStore(int failurePoint)
    {
        if (failurePoint == 1) throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedArgumentAssignmentsKeepThePreviousValue(bool fail)
    {
        Assert.Equal(fail ? "old" : "new", AssignInTry("old", fail));
    }

    static string AssignInTry(string input, bool fail)
    {
        try { input = Normalize(fail); }
        catch (FormatException) { }
        return input;
    }

    [DoNotOptimize]
    static string Normalize(bool fail) => fail ? throw new FormatException() : "new";

    [Theory, InlineData(0), InlineData(1)]
    [CheckCodeGenAfterRun]
    public void MultiExit_Finally1(int path)
    {
        int x = path;

        try {
            try {
                Utils.DoNotOptimize(x); // fake side effect

                if (x > 0) {
                    goto Target1;
                } else {
                    goto Target2;
                }
            } finally {
                x += 10;
            }
        Target1:
            x += 5;
        Target2:
            x += 2;
        } finally {
            Assert.Equal(path == 0 ? 12 : 18, x);
        }

        // CHECK: try finally
        // CHECK: leave [[target1:.*]]
        // CHECK: leave [[target2:.*]]
        // CHECK: resume [[target1]], [[target2]]
        // CHECK: leave [[outerExit:.*]]
        // CHECK: resume [[outerExit]]
    }

    [Fact]
    public void CrossingVars_Catch1()
    {
        int x = 1;
        string str = "1;0";

        try {
            int y = int.Parse(str.Split(';')[1]);
            x = 2;
            x = 100 / y;
        } catch (DivideByZeroException) {
            Assert.Equal(2, x);
            x = 400;
        }
        Assert.Equal(400, x);
    }

    [Fact]
    public void CrossingVars_Finally1()
    {
        int x = 1;
        string str = "123";

        try {
            x = int.Parse(str) * 1000;
        } finally {
            x *= 2;
        }
        Assert.Equal(123 * 1000 * 2, x);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(4, -1)]
    [InlineData(4, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(12, 12)]
    [InlineData(13, 12)]
    [InlineData(13, 13)]
    [InlineData(33, -1)]
    [InlineData(33, 0)]
    [InlineData(33, 9)]
    [InlineData(33, 33)]
    [InlineData(33, 40)]
    [CheckCodeGenAfterRun]
    public void GuardedArrayMapsPreserveExceptionRegions(int count, int destinationLength)
    {
        var source = Utils.DoNotOptimize(new int[count]);
        var destination = Utils.DoNotOptimize(destinationLength < 0 ? null : new int[destinationLength]);
        Exception? failure = null;
        int finallyCount = 0;

        try {
            for (int i = 0; i < source.Length; i++) {
                source[i] = i + 7;
                destination![i] = source[i] * 3;
            }
        } catch (Exception error) {
            failure = error;
        } finally {
            finallyCount++;
        }

        CheckArrayMapResult(source, destination, failure, finallyCount);

        // CHECK: Vector256
    }

    [DoNotOptimize]
    static void CheckArrayMapResult(int[] source, int[]? destination, Exception? failure, int finallyCount)
    {
        Assert.Equal(1, finallyCount);
        int completed = Math.Min(source.Length, destination?.Length ?? 0);
        if (completed == source.Length) {
            Assert.Null(failure);
        } else if (destination == null) {
            Assert.IsType<NullReferenceException>(failure);
        } else {
            Assert.IsType<IndexOutOfRangeException>(failure);
        }

        // The source store precedes the destination access in each iteration.
        int sourceStores = failure == null ? completed : completed + 1;
        Assert.Equal(Enumerable.Range(0, source.Length).Select(i => i < sourceStores ? i + 7 : 0), source);
        if (destination != null) {
            Assert.Equal(Enumerable.Range(0, destination.Length).Select(i => i < completed ? (i + 7) * 3 : 0), destination);
        }
    }
}
