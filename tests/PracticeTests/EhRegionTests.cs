namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class EhRegionTests
{
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
}
