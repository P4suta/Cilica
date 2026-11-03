namespace DistIL.PracticeTests;

using DistIL.Attributes;
using System.Collections;
using System.Runtime.CompilerServices;

[Optimize, CheckCodeGenAfterRun]
public class ExpandLinqTests
{
    [Fact]
    public void MutableListFiltersPreserveVersionChecks()
    {
        var source = new List<int> { 1, 2, 3 };
        bool threw = false;

        try {
            _ = source.Where(x => {
                if (x == 1) {
                    source.Add(4);
                }

                return true;
            }).Aggregate(0, (sum, x) => sum + x);
        } catch (InvalidOperationException) {
            threw = true;
        }

        Assert.True(threw);
        Assert.Equal(4, source.Count);
        // CHECK: Enumerable::Where
        // CHECK-NOT: Enumerable::Aggregate
    }

    [Fact]
    public void MutableListProjectionUsesLibraryMaterialization()
    {
        var baseline = new List<int> { 1, 2, 3 };
        var expected = ReferenceProjection(baseline);
        var source = new List<int> { 1, 2, 3 };
        var actual = source.Select(x => Mutate(source, x)).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(baseline, source);
        // CHECK: Enumerable::Select
        // CHECK: Enumerable::ToArray
    }

    [Fact]
    public void InterfaceTypedListsPreserveLibraryDispatch()
    {
        IEnumerable<int> source = Utils.DoNotOptimize<IEnumerable<int>>(new AlternateList { 1, 2, 3 });

        Assert.Equal(6, source.Where(x => true).Aggregate(0, (sum, x) => sum + x));
        Assert.Equal(42, source.Aggregate(0, (sum, x) => sum + x));
        // CHECK: Enumerable::Where
    }

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    static int[] ReferenceProjection(List<int> source) => source.Select(x => Mutate(source, x)).ToArray();

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    static int Mutate(List<int> source, int value)
    {
        if (value == 1) {
            source.Add(4);
        }

        return value;
    }

    [DoNotOptimize]
    sealed class AlternateList : List<int>, IEnumerable<int>
    {
        IEnumerator<int> IEnumerable<int>.GetEnumerator() => ((IEnumerable<int>)new[] { 42 }).GetEnumerator();
    }

    [Fact]
    public void CastUnboxesValueTypes()
    {
        object[] source = [1, 2, 3];
        Assert.Equal(new[] { 1, 2, 3 }, source.Cast<int>().ToArray());
        // CHECK: CIL::UnboxObj
        // CHECK-NOT: call Enumerable
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 3)]
    public void ArrayTakeClampsItsCount(int count, int expectedLength)
    {
        int[] source = [1, 2, 3];
        var result = source.Take(count).ToArray();
        Assert.Equal(expectedLength, result.Length);
        for (int i = 0; i < result.Length; i++) Assert.Equal(i + 1, result[i]);
        // CHECK-NOT: call Enumerable
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(10, 3, 4)]
    public void TakeStopsBeforeAcquisitionAndAdvancement(int count, int expectedLength, int expectedMoves)
    {
        var source = new TrackingEnumerable([1, 2, 3]);
        var result = source.Select(x => x).Take(count).ToArray();
        Assert.Equal(expectedLength, result.Length);
        Assert.Equal(expectedMoves, source.Moves);
        Assert.Equal(count > 0 ? 1 : 0, source.Acquisitions);
        Assert.Equal(count > 0 ? 1 : 0, source.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Theory]
    [InlineData(-3, 2)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue - 2, 3)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, 0)]
    public void RangeHandlesSignedAndInclusiveBounds(int start, int count)
    {
        var result = Enumerable.Range(start, count).Select(x => x).ToArray();
        Assert.Equal(count, result.Length);
        for (int i = 0; i < result.Length; i++) Assert.Equal(checked(start + i), result[i]);
        // CHECK: ThrowIfNegative
        // CHECK: ThrowIfGreaterThan
        // CHECK-NOT: call Enumerable
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(int.MaxValue, 2)]
    public void RangeValidatesWhenTheQueryIsCreated(int start, int count)
    {
        int reached = 0;
        string? parameter = null;
        try {
            var query = Enumerable.Range(start, count);
            reached = 1;
            _ = query.Select(x => x).ToArray();
        } catch (ArgumentOutOfRangeException ex) { parameter = ex.ParamName; }
        Assert.Equal(0, reached);
        Assert.Equal("count", parameter);
        // CHECK: ThrowIfNegative
        // CHECK: ThrowIfGreaterThan
    }

    [Fact]
    public void EmptyQueriesValidatePredicatesEagerly()
    {
        var predicate = Utils.DoNotOptimize<Func<int, bool>>(null!);
        int reached = 0;
        string? parameter = null;
        try {
            var query = Array.Empty<int>().Where(predicate);
            reached = 1;
            _ = query.ToArray();
        } catch (ArgumentNullException ex) { parameter = ex.ParamName; }
        Assert.Equal(0, reached);
        Assert.Equal("predicate", parameter);
        // CHECK: ThrowIfNull
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void SelectManySupportsSelectorOverloads()
    {
        int[] source = [10, 20];
        Assert.Equal(new[] { 10, 11, 20, 21 }, source.SelectMany(x => new[] { x, x + 1 }).ToArray());
        Assert.Equal(new[] { 0, 1 }, source.SelectMany((x, i) => new[] { i }).ToArray());
        Assert.Equal(new[] { 11, 21 }, source.SelectMany(x => new[] { 1 }, (x, y) => x + y).ToArray());
        Assert.Equal(new[] { 10, 21 }, source.SelectMany((x, i) => new[] { i }, (x, y) => x + y).ToArray());
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void SelectManyDisposesInnerAndOuterOnFirst()
    {
        var outer = new TrackingEnumerable([1, 2]);
        var inner = new TrackingEnumerable([10, 20]);
        int result = outer.SelectMany(_ => inner).First();
        Assert.Equal(10, result);
        Assert.Equal(1, outer.Moves);
        Assert.Equal(1, inner.Moves);
        Assert.Equal(1, outer.Disposals);
        Assert.Equal(1, inner.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SelectManyTakeStopsEveryEnumerator(int count)
    {
        var outer = new TrackingEnumerable([1, 2]);
        var inner = new TrackingEnumerable([10, 20]);
        var result = outer.SelectMany(_ => inner).Take(count).ToArray();
        Assert.Equal(count, result.Length);
        Assert.Equal(count, outer.Moves);
        Assert.Equal(count, inner.Moves);
        Assert.Equal(count, outer.Acquisitions);
        Assert.Equal(count, inner.Acquisitions);
        Assert.Equal(count, outer.Disposals);
        Assert.Equal(count, inner.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void SelectManyDisposesOnSelectorException()
    {
        var outer = new TrackingEnumerable([1, 2]);
        var inner = new TrackingEnumerable([10, 20]);
        bool threw = false;
        try { _ = outer.SelectMany(_ => inner, (x, y) => ThrowAtTwenty(y)).ToArray(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
        Assert.Equal(1, outer.Disposals);
        Assert.Equal(1, inner.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void TakeCountsItemsBeforeDownstreamFiltering()
    {
        var source = new TrackingEnumerable([1, 2, 3]);
        var result = source.Take(2).Where(x => x < 0).ToArray();
        Assert.Empty(result);
        Assert.Equal(2, source.Moves);
        Assert.Equal(1, source.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void TakeBeforeSelectManyCountsOuterItems()
    {
        var outer = new TrackingEnumerable([1, 2]);
        var inner = new TrackingEnumerable([10, 20]);
        var result = outer.Take(1).SelectMany(_ => inner).ToArray();
        Assert.Equal(new[] { 10, 20 }, result);
        Assert.Equal(1, outer.Moves);
        Assert.Equal(3, inner.Moves);
        Assert.Equal(1, outer.Disposals);
        Assert.Equal(1, inner.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void NestedSelectManyDisposesFromInnerToOuter()
    {
        var order = new List<int>();
        var outer = new TrackingEnumerable([1], () => order.Add(1));
        var middle = new TrackingEnumerable([2], () => order.Add(2));
        var inner = new TrackingEnumerable([3], () => order.Add(3));
        int value = outer.SelectMany(_ => middle).SelectMany(_ => inner).First();
        Assert.Equal(3, value);
        Assert.Equal(new[] { 3, 2, 1 }, order);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void FlattenedFindAndQuantifyPreserveDefaultsAndDisposal()
    {
        var inner = new TrackingEnumerable([10, 20]);
        int[] empty = [];
        Assert.Equal(99, empty.SelectMany(_ => inner).FirstOrDefault(99));
        Assert.Equal(0, inner.Acquisitions);
        int[] outer = [1, 2];
        bool any = outer.SelectMany(_ => inner).Any(x => x == 10);
        Assert.True(any);
        Assert.Equal(1, inner.Disposals);
        bool all = outer.SelectMany(_ => inner).All(x => x < 20);
        Assert.False(all);
        Assert.Equal(2, inner.Disposals);
        // CHECK: try finally
        // CHECK-NOT: call Enumerable
    }

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    static int ThrowAtTwenty(int value) => value == 20 ? throw new InvalidOperationException() : value;

    [DoNotOptimize]
    sealed class TrackingEnumerable(int[] values, Action? onDispose = null) : IEnumerable<int>
    {
        public int Acquisitions, Moves, Disposals;
        public IEnumerator<int> GetEnumerator() { Acquisitions++; return Iterate().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        IEnumerable<int> Iterate()
        {
            try {
                foreach (int value in values) { Moves++; yield return value; }
                Moves++;
            } finally { Disposals++; onDispose?.Invoke(); }
        }
    }

    [Fact]
    public void Array_Map_PredCount()
    {
        int[] source = [
            79, 133, 11, 155, 151, 190, 3, 24, 196, 118, 97, 67, 40, 62, 18, 179,
            136, 81, 73, 15, 31, 242, 47, 1, 58, 50, 123, 89, 203, 52, 54, 92
        ];
        int count = source.Select(x => x - 128).Count(x => x > 0 && x < 64);
        Assert.Equal(6, count);

        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void Range_Map_ToList()
    {
        var result = Enumerable.Range(0, 13).Select(x => x * 2).ToList();

        Assert.Equal([0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24], result);

        // CHECK-NOT: call Enumerable
        // CHECK: newobj List`1[int]::.ctor(int: 13)
        // CHECK: Add(
    }

    [Fact]
    public void Array_CastFilterMap_ToArray()
    {
        object[] source = [
            "2:dolor", 9365, "11:tempor", "13:ut", "9:do", 9525, 6433, 4785, 8846,
            "3:sit", "15:et", "1:ipsum", "10:eiusmod", 7298, 4809, "0:lorem",
            "14:labore", "8:sed", "4:amet", "5:consectetur", "7:elit", "12:incididunt",
            8983, "6:adipiscing", 7882, 2343, 6152, 3178, 5586, 1282, 7126, 9023
        ];
        string[] expected = "dolor tempor ut do sit et ipsum eiusmod lorem labore sed amet consectetur elit incididunt adipiscing".Split(' ');

        var results = source
            .OfType<string>()
            .Where(s => s.Length > 0)
            .Select(s => s.Split(':')[1])
            .ToArray();

        Assert.Equal(expected, results);

        // CHECK-NOT: call Enumerable
    }
    
    [Fact]
    public void Range_Aggregate()
    {
        var result = Enumerable.Range(1, 50).Select(x => x * x).Aggregate(0, (r, x) => r + x);
        Assert.Equal(42925, result);

        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void String_Count()
    {
        string text = "The Quick Brown Fox Jumped Over The Lazy Dog";
        int upperLetters = text.Count(ch => ch is >= 'A' and <= 'Z');
        Assert.Equal(9, upperLetters);

        // CHECK-NOT: call Enumerable
    }

    [Fact]
    public void Array_TakeAggregate_SideEffect()
    {
        string[] arr = ["100", "28", "not a number"];

        int result = arr.Select(s => int.Parse(s)).Take(2).Aggregate(0, (r, x) => r + x);

        Assert.Equal(128, result);

        // CHECK-NOT: call Enumerable
    }
    
    [Fact]
    public void Disposeable_Enumerable_Count()
    {
        var source = "The Quick Brown Fox Jumped Over The Lazy Dog".Split(' ').AsEnumerable();
        int result = source.Count(s => !s.Contains('x'));

        Assert.Equal(8, result);

        // CHECK: try finally
        // CHECK-NOT: call Enumerable
        // CHECK: callvirt IDisposable::Dispose
    }

    [Fact]
    public void StructEnumerable_Count()
    {
        var source = new ArraySegment<string>("The Quick Brown Fox Jumped Over The Lazy Dog".Split(' '));
        int result = source.Count(s => !s.Contains('x'));

        Assert.Equal(8, result);

        // CHECK: try finally
        // CHECK: GetEnumerator
        // CHECK-SAME: ArraySegment`1+Enumerator[string]
    }

    [Fact]
    public void Enumerable_Filter_Loop_PhiHeader()
    {
        var source = "the quick brown Fox jumped over the Lazy Dog".Split(' ').Where(s => !string.IsNullOrEmpty(s));
        var result = new List<string>();

        var itr = source.GetEnumerator();
        try {
            for (int i = 0; itr.MoveNext(); ) {
                string word = itr.Current;
                i++;
                if (!char.IsUpper(word[0])) continue; // backedge

                result.Add(string.Format("{0}:{1}", i, word)); // box to prevent i from being addr taken
            }
        } finally {
            (itr as IDisposable)?.Dispose();
        }

        Assert.Equal("4:Fox 8:Lazy 9:Dog".Split(' '), result);

        // CHECK: Where<string>(
        // CHECK: phi
        // CHECK-NEXT: MoveNext(
    }
}
