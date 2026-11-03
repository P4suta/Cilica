namespace DistIL.PracticeTests;

using System.Runtime.CompilerServices;

using DistIL.Attributes;

[Optimize, CheckCodeGenAfterRun]
public class PresizeListsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void BuildArray_MultipleAppendsPreserveOrder(int count)
    {
        var list = new List<int>();
        for (int i = 0; i < count; i++) { list.Add(i); list.Add(i + 100); }
        var actual = list.ToArray();
        Assert.Equal(count > 0 ? new[] { 0, 100, 1, 101, 2, 102 } : Array.Empty<int>(), actual);
        // CHECK: CIL::NewArray
        // CHECK-NOT: Add(
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AppendExceptionsExposeOnlyCompletedItems(int failAt)
    {
        var list = new List<int>();
        try {
            for (int i = 0; i < 3; i++) list.Add(Produce(i, failAt));
        } catch (InvalidOperationException) { }
        Assert.Equal(failAt, list.Count);
        for (int i = 0; i < list.Count; i++) Assert.Equal(i, list[i]);
        // CHECK: newobj List`1[int]::.ctor()
        // CHECK: try catch
        // CHECK: Add(
    }

    [Fact]
    public void AppendsInvalidateExistingEnumerators()
    {
        var list = new List<int>();
        var iterator = list.GetEnumerator();
        for (int i = 0; i < 3; i++) list.Add(i);
        bool threw = false;
        try { iterator.MoveNext(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
        // CHECK: Add(
    }

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    private static int Produce(int value, int failAt)
        => value == failAt ? throw new InvalidOperationException() : value;

    [Fact]
    public void BuildArray_ForLoop()
    {
        var list = new List<string>();

        for (int i = 0; i < 32; i++) {
            int j = i + 1;
            string s = (j % 3, j % 5) switch {
                (0, 0) => "FizzBuzz",
                (0, _) => "Fizz",
                (_, 0) => "Buzz",
                _ => string.Format("{0}", j) // box to prevent i from being addr taken
            };
            list.Add(s);
        }
        var array = list.ToArray(); // last use

        Assert.Equal(32, array.Length);
        Assert.Equal("1", array[0]);
        Assert.Equal("2", array[1]);
        Assert.Equal("Fizz", array[2]);
        Assert.Equal("4", array[3]);
        Assert.Equal("Buzz", array[4]);
        Assert.Equal("FizzBuzz", array[14]);

        // CHECK: CIL::NewArray
        // CHECK: GetArrayDataReference
        // CHECK-NOT: Add(
        // CHECK-NOT: ToArray(
    }

    [Fact]
    public void BuildArray_EnumerableLoop()
    {
        var list = new List<string>();
        var source = CreateList().AsReadOnly();
        int index = 0;

        foreach (string str in source) {
            list.Add(index + ": " + str);
            index++;
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length);
        Assert.Equal("0: lorem", array[0]);
        Assert.Equal("4: amet", array[4]);

        // CHECK: get_Count
        // CHECK: CIL::NewArray
        // CHECK-NOT: Add(
        // CHECK-NOT: ToArray(
    }

    [Fact]
    public void BuildArray_EnumerableLoop_CondAdd()
    {
        var list = new List<string>();
        var source = CreateList().AsReadOnly();

        foreach (string str in source) {
            if (!string.IsNullOrWhiteSpace(str)) {
                list.Add(str);
            }
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length);
        Assert.Equal("lorem", array[0]);
        Assert.Equal("amet", array[4]);

        // CHECK: List`1[string]::.ctor()
        // CHECK-NOT: EnsureCapacity(
        // CHECK: Add(
    }

    [Fact]
    public void BuildArray_EnumerableLoop_ClobberAdd()
    {
        var list = new List<string>();

        list.Add("First");
        var source = CreateList().AsReadOnly();

        foreach (string str in source) {
            list.Add(str);
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length - 1);
        Assert.Equal("First", array[0]);
        Assert.Equal("lorem", array[1]);
        Assert.Equal("amet", array[5]);

        // CHECK: Add(this: {{\w+}}, string: "First")
        // CHECK: get_Count(
        // CHECK: EnsureCapacity(
        // CHECK: Add(
    }

    [Fact]
    public void AppendTo_ExternalList_ForLoop()
    {
        var list = CreateList();
        var prevItems = list.ToArray();
        int prevCount = list.Count;

        for (int i = 0; i < 16; i++) {
            string str = string.Format("{0}", i);
            list.Add(str);
        }

        Assert.Equal(16, list.Count - prevCount);
        Assert.Equal(prevItems, list.Take(prevCount));
        Assert.Equal("0", list[prevCount]);
        Assert.Equal("15", list[^1]);

        // CHECK: Format(
        // CHECK: Add(
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> CreateList() => ["lorem", "ipsum", "dolor", "sit", "amet"];
}
