# Optimization Passes

- [LINQ Expansion](#linq-expansion)
- [Loop Strength Reduction](#loop-strength-reduction)
- [Loop Vectorization](#loop-vectorization)
- [Pre-size Lists / Array Builder Opts](#pre-size-lists--array-builder-opts)
- [Scalar Replacement](#scalar-replacement)
- [Reflection Folding](#reflection-folding)
- [Algebraic Simplification](#algebraic-simplification)
- [Common Sub-expression Elimination](#common-sub-expression-elimination)
- [Assertion Propagation](#assertion-propagation)
- [Devirtualization](#devirtualization)
- [Method Inlining](#method-inlining)
- [Pipeline and Compiler Benchmarks](#pipeline-and-compiler-benchmarks)

Cilica also implements canonicalization, SSA promotion, control-flow simplification, and dead-code elimination. The default pipeline is defined by `CompilationPipeline.CreateDefault()`.

Code examples show source and representative equivalent output, with compiler-generated delegate setup and temporary names simplified for readability. Decompiled unsafe operations correspond to lifted IL instructions; exact output depends on the surrounding method and subsequent simplification.

The benchmark tables compare .NET 10 with the same runtime plus Cilica.

## LINQ Expansion

Rewrites recognized LINQ queries into imperative code. Compatible sources, intermediate stages, and consuming operations fuse into a loop, while operations with unproven collection semantics remain library calls. Queries can also fuse across helper methods when inlining exposes their sources and stages.

### Examples

#### Filtering and aggregation

An array provides fixed storage and a stable element count for this query:

```cs
[Optimize]
public static float Aggregate(Item[] items)
    => items
        .Select(x => x.Weight)
        .Where(x => x > 0.0f && x < 1.0f)
        .Aggregate(0.0f, (result, x) => result + (x < 0.5f ? -x : x));
```

The fused iteration has this shape:

```cs
public static float Aggregate(Item[] items)
{
    ArgumentNullException.ThrowIfNull(items, "source");
    float result = 0.0f;

    foreach (Item item in items) {
        float value = item.Weight;

        if (value > 0.0f && value < 1.0f) {
            result += value < 0.5f ? -value : value;
        }
    }

    return result;
}
```

For a mutable `List<T>` source, the optimizer retains the relevant library stage so that iterator version checks, indexed dispatch, and materialization behavior remain intact. It does not traverse a list's private backing array. A downstream aggregation may still expand around the retained iterator, including its disposal scope.

#### Fusion through helpers and checked sums

A helper returning `IEnumerable<T>` can still expose a query over an array after inlining:

```cs
static IEnumerable<int> PositiveValues(int[] values)
    => values.Where(value => value > 0);

[Optimize]
public static int SumPositiveTriples(int[] values)
    => PositiveValues(values).Select(value => unchecked(value * 3)).Sum();
```

The filter, projection, and sum can share one traversal:

```cs
public static int SumPositiveTriples(int[] values)
{
    ArgumentNullException.ThrowIfNull(values, "source");
    int sum = 0;

    foreach (int value in values) {
        if (value > 0) {
            int projected = unchecked(value * 3);
            sum = checked(sum + projected);
        }
    }

    return sum;
}
```

Non-nullable `int` and `long` sums support staged queries and selector overloads such as `values.Sum(value => (long)value * value)`. Accumulation remains checked even when the selector uses unchecked arithmetic. An overflow stops before the next selector invocation and preserves enumerator disposal. These checked reductions remain scalar.

Direct `source.Sum()` calls without a preceding stage retain the runtime's specialized implementation. Floating-point, decimal, and nullable sums also remain library calls.

### Benchmarks

[Source](../tests/Benchmarks/LinqExpansion.cs). These workloads include mutable-list queries, a string predicate count, and a LINQ ray tracer. Their results measure the complete default pipeline together with the runtime JIT, rather than one isolated pass.

| Method         | Toolchain          | ElemCount |       Mean |      Error |     StdDev | Ratio | RatioSD |
| -------------- | ------------------ | --------- | ---------: | ---------: | ---------: | ----: | ------: |
| FilterObjects  | .NET 10.0          | 4096      | 18.0047 μs | 0.64129 μs | 0.42417 μs |  1.00 |    0.03 |
| FilterObjects  | .NET 10.0 + Cilica | 4096      | 11.3843 μs | 0.38509 μs | 0.25471 μs |  0.63 |    0.02 |
|                |                    |           |            |            |            |       |         |
| FirstPredicate | .NET 10.0          | 4096      |  7.1043 μs | 0.15805 μs | 0.09405 μs |  1.00 |    0.02 |
| FirstPredicate | .NET 10.0 + Cilica | 4096      |  6.7846 μs | 0.08065 μs | 0.05334 μs |  0.96 |    0.01 |
|                |                    |           |            |            |            |       |         |
| Aggregate      | .NET 10.0          | 4096      | 30.6857 μs |  0.3194 μs | 0.21126 μs |  1.00 |    0.01 |
| Aggregate      | .NET 10.0 + Cilica | 4096      | 12.1921 μs | 0.85662 μs |  0.5666 μs |  0.40 |    0.02 |
|                |                    |           |            |            |            |       |         |
| CountLetters   | .NET 10.0          | 4096      |  3.3553 μs | 0.01978 μs | 0.01177 μs |  1.00 |    0.00 |
| CountLetters   | .NET 10.0 + Cilica | 4096      |  0.2108 μs | 0.00223 μs | 0.00147 μs |  0.06 |    0.00 |
|                |                    |           |            |            |            |       |         |
| RayTracer      | .NET 10.0          | 4096      |   9,013 μs |   200.1 μs |   119.1 μs |  1.00 |    0.02 |
| RayTracer      | .NET 10.0 + Cilica | 4096      |   4,870 μs |   105.6 μs |    69.8 μs |  0.54 |    0.01 |

### Features

**Supported sources**

- Arrays, strings, and `Enumerable.Range()` have specialized iteration when their storage and cardinality contracts apply.
- `List<T>` and other mutable indexed sources retain library operations whose behavior depends on their concrete collection type.
- Other `IEnumerable<T>` sources use enumerators, with acquisition, iteration, and disposal preserved.

**Supported stages**

- `Where`, `Select`, `OfType`, and `Cast`.
- `SelectMany`, including nested enumeration and disposal.
- Integer-count `Skip` and `Take`.

**Supported sinks**

- `ToList`, `ToArray`, `ToHashSet`, and `ToDictionary`.
- `Aggregate` and predicate `Count`.
- Non-nullable `int` and `long` `Sum` for staged queries and selector overloads.
- `First` and `FirstOrDefault`.
- Predicate `Any` and `All`.
- Recognized consuming loops.

**Semantic constraints**

- Source and delegate argument validation remains at query construction sites, even when iteration never begins.
- `Take` guards can stop before acquiring or advancing an enumerator. Nested enumerators are disposed from inner to outer scope on early exit and exceptions.
- Indexed delegate overloads retain checked index increments. Counts over sources without a proven bounded cardinality retain overflow checks.
- Unknown comparer, iterator, or indexed-collection dispatch prevents transformations that would replace its behavior with a different library path.
- Arbitrary user-defined `Count` getters are not introduced to estimate sequence length.

## Loop Strength Reduction

Canonical counting loops can replace array or span indexing with pointer induction when the loop bound proves each access is within the same storage. This exposes sequential accesses to the vectorizer and reduces repeated address calculation.

Dominating equality and range guards can establish array bounds, including a count that covers only a prefix of an array. Prefix loops retain their original count. Array-length loop bounds can be hoisted even when the bounding array is not indexed by the body.

The pass preserves covariant array-store checks and declines loops whose span view may be reassigned during iteration. It accesses arrays through `MemoryMarshal.GetArrayDataReference()` and spans through their public reference accessors. Loop analysis tracks nesting and combines backedges sharing a header.

CIL preparation can select ordinary array instructions for bounded scalar accesses, exposing the array and index relationship to the runtime JIT. Vector accesses, reinterpretation, volatile or unaligned memory operations, and reference stores with unproven array-type compatibility retain pointer-based access.

### Example

A range check can prove that a loop touches only a valid prefix:

```cs
[Optimize]
public static void FillPrefix(int[] values, int count)
{
    if ((uint)count > (uint)values.Length) {
        throw new ArgumentOutOfRangeException(nameof(count));
    }

    for (int i = 0; i < count; i++) {
        values[i] = 7;
    }
}
```

The scalar access pattern can use a shared base reference while retaining `count` as the loop bound:

```cs
public static void FillPrefix(int[] values, int count)
{
    if ((uint)count > (uint)values.Length) {
        throw new ArgumentOutOfRangeException(nameof(count));
    }

    ref int first = ref MemoryMarshal.GetArrayDataReference(values);
    for (int i = 0; i < count; i++) {
        Unsafe.Add(ref first, i) = 7;
    }
}
```

Elements beyond the prefix remain unchanged. The same bound proof can enable vectorization without another array-length guard.

## Loop Vectorization

Loop vectorization is enabled in the default pipeline. Eligible innermost loops use `Vector256<T>` operations when hardware accelerated, followed by `Vector128<T>` operations for the remaining full vectors. The 128-bit path also handles the whole loop when only 128-bit acceleration is available. Each width has its own runtime hardware-acceleration check; a scalar loop handles any remaining elements or execution without vector acceleration.

When an array extent cannot be proved statically, the pass adds a null check followed by a length check before the vector path. This allows pointwise loops over multiple arrays and loops bounded by a count parameter to vectorize. Each unknown array extent is checked once, and short loops skip these checks. Existing dominating range guards avoid redundant runtime checks.

The original scalar loop serves both as the tail and as the fallback when a guard fails. A shorter or null array therefore retains the original exception timing and completed stores. Reduction seeds and final counter values are preserved on both paths.

### Examples

#### Predicate counts

A string predicate count with a simple comparison can use one mask lane per character:

```cs
[Optimize]
public static int CountAsciiA(string text)
{
    return text.Count(Matches);

    static bool Matches(char character) => character == 'A';
}
```

Its vector loop has the following shape. Each matching UTF-16 lane contributes two set byte bits, so the population count is divided by two:

```cs
public static int CountAsciiA(string text)
{
    ArgumentNullException.ThrowIfNull(text, "source");
    ref char first = ref MemoryMarshal.GetReference(text.AsSpan());
    int index = 0;
    int count = 0;

    if (Vector256.IsHardwareAccelerated) {
        var target = Vector256.Create((ushort)'A');
        for (; (long)text.Length - index >= 16; index += 16) {
            ref char current = ref Unsafe.Add(ref first, index);
            var values = Unsafe.ReadUnaligned<Vector256<ushort>>(ref Unsafe.As<char, byte>(ref current));
            var matches = Vector256.Equals(values, target);
            count += BitOperations.PopCount(matches.AsByte().ExtractMostSignificantBits()) >> 1;
        }
    }

    if (Vector128.IsHardwareAccelerated) {
        var target = Vector128.Create((ushort)'A');
        for (; (long)text.Length - index >= 8; index += 8) {
            ref char current = ref Unsafe.Add(ref first, index);
            var values = Unsafe.ReadUnaligned<Vector128<ushort>>(ref Unsafe.As<char, byte>(ref current));
            var matches = Vector128.Equals(values, target);
            count += BitOperations.PopCount(matches.AsByte().ExtractMostSignificantBits()) >> 1;
        }
    }

    for (; index < text.Length; index++) {
        count += Unsafe.Add(ref first, index) == 'A' ? 1 : 0;
    }

    return count;
}
```

#### Generated sequences

Array and by-value span loops can also generate a sequence from their induction variable:

```cs
[Optimize]
public static void GenerateInts(Span<int> destination, int bias)
{
    for (int i = 0; i < destination.Length; i++) {
        destination[i] = i * 3 + bias;
    }
}
```

The widened induction variable adds a constant lane-offset vector. Invariant vectors are formed outside the loop:

```cs
public static void GenerateInts(Span<int> destination, int bias)
{
    ref int first = ref destination.GetPinnableReference();
    int index = 0;

    if (Vector256.IsHardwareAccelerated) {
        var offsets = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var scale = Vector256.Create(3);
        var biasVector = Vector256.Create(bias);
        for (; (long)destination.Length - index >= 8; index += 8) {
            var indices = Vector256.Create(index) + offsets;
            var values = indices * scale + biasVector;
            ref int current = ref Unsafe.Add(ref first, index);
            Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref current), values);
        }
    }

    if (Vector128.IsHardwareAccelerated) {
        var offsets = Vector128.Create(0, 1, 2, 3);
        var scale = Vector128.Create(3);
        var biasVector = Vector128.Create(bias);
        for (; (long)destination.Length - index >= 4; index += 4) {
            var indices = Vector128.Create(index) + offsets;
            var values = indices * scale + biasVector;
            ref int current = ref Unsafe.Add(ref first, index);
            Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref current), values);
        }
    }

    for (; index < destination.Length; index++) {
        Unsafe.Add(ref first, index) = index * 3 + bias;
    }
}
```

#### Runtime bounds guards

Pointwise array loops can vectorize even when the destination length is unknown at compile time:

```cs
[Optimize]
public static void Map(int[] source, int[] destination)
{
    for (int i = 0; i < source.Length; i++) {
        destination[i] = unchecked(source[i] * 3 + 7);
    }
}
```

The vector path requires room for the whole traversal. A failed guard leaves the original scalar loop to perform its stores and raise any exception:

```cs
public static void Map(int[] source, int[] destination)
{
    int length = source.Length;
    int index = 0;

    if (length >= 4 && destination != null && destination.Length >= length) {
        ref int sourceBase = ref MemoryMarshal.GetArrayDataReference(source);
        ref int destinationBase = ref MemoryMarshal.GetArrayDataReference(destination);

        if (Vector256.IsHardwareAccelerated) {
            var scale = Vector256.Create(3);
            var bias = Vector256.Create(7);
            for (; (long)length - index >= 8; index += 8) {
                ref int input = ref Unsafe.Add(ref sourceBase, index);
                ref int output = ref Unsafe.Add(ref destinationBase, index);
                var values = Unsafe.ReadUnaligned<Vector256<int>>(ref Unsafe.As<int, byte>(ref input));
                Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref output), values * scale + bias);
            }
        }

        if (Vector128.IsHardwareAccelerated) {
            var scale = Vector128.Create(3);
            var bias = Vector128.Create(7);
            for (; (long)length - index >= 4; index += 4) {
                ref int input = ref Unsafe.Add(ref sourceBase, index);
                ref int output = ref Unsafe.Add(ref destinationBase, index);
                var values = Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.As<int, byte>(ref input));
                Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref output), values * scale + bias);
            }
        }
    }

    for (; index < length; index++) {
        destination[index] = unchecked(source[index] * 3 + 7);
    }
}
```

An empty source does not access the destination. A short destination receives the same completed stores before `IndexOutOfRangeException`. Passing the same array for both arguments is valid for this pointwise map; loops with dependencies between different indices remain scalar.

### Benchmarks

[Source](../tests/Benchmarks/AutoVectorization.cs). `CountLetters` uses vector character comparisons and population counts; `GenerateFloats` vectorizes its pointwise arithmetic. `GenerateInts` retains scalar integer `Abs` to preserve overflow exceptions. `NormalizeFloats` retains its scalar floating-point min/max reduction and vectorizes its pointwise normalization loop.

| Method          | Toolchain          | ElemCount |        Mean |     Error |    StdDev | Ratio | Code Size |
| --------------- | ------------------ | --------- | ----------: | --------: | --------: | ----: | --------: |
| CountLetters    | .NET 10.0          | 4096      |  3,276.2 ns |  35.17 ns |  23.26 ns |  1.00 |     450 B |
| CountLetters    | .NET 10.0 + Cilica | 4096      |    206.7 ns |   1.41 ns |   0.94 ns |  0.06 |     313 B |
|                 |                    |           |             |           |           |       |           |
| GenerateInts    | .NET 10.0          | 4096      |  1,182.6 ns |  10.93 ns |   7.23 ns |  1.00 |      84 B |
| GenerateInts    | .NET 10.0 + Cilica | 4096      |  1,179.9 ns |   8.44 ns |   5.02 ns |  1.00 |      84 B |
|                 |                    |           |             |           |           |       |           |
| GenerateFloats  | .NET 10.0          | 4096      |  2,790.9 ns |  31.57 ns |  20.88 ns |  1.00 |   1,724 B |
| GenerateFloats  | .NET 10.0 + Cilica | 4096      |    597.2 ns |   2.57 ns |   1.70 ns |  0.21 |   1,873 B |
|                 |                    |           |             |           |           |       |           |
| NormalizeFloats | .NET 10.0          | 4096      | 11,185.7 ns |  73.48 ns |  48.60 ns |  1.00 |   1,798 B |
| NormalizeFloats | .NET 10.0 + Cilica | 4096      | 10,682.8 ns | 177.47 ns | 117.38 ns |  0.96 |   1,892 B |

### Features

**Supported operations**

- Memory: nonvolatile, unit-stride loads and stores with storage extents proved statically or guarded at runtime. Addresses use either an invariant base plus the counter or a sequential pointer counter.
- Arithmetic: unchecked `+`, `-`, `*`, floating-point `/`, negation, `&`, `|`, `^`, and `~`.
- Shifts: `<<`, `>>`, and `>>>` with invariant scalar shift counts and compatible signedness.
- Math: floating-point `Abs`, `Sqrt`, `Floor`, and `Ceiling`; integer `Min` and `Max`.
- Comparisons and masks: supported ordered comparisons and equality, mask combinations, conditional selection, and predicated counts.
- Conversion: `int` to `float` with the same lane count.
- Types: supported numeric primitives with uniform lane widths. Character comparisons use unsigned 16-bit lanes.
- Reductions: integer `+=`, `*=`, `&=`, `|=`, `^=`, `Min`, `Max`, and predicated counts, preserving the initial seed and unchecked wrapping.
- Simple conditions exposed as selects or boolean operations by CFG simplification can be widened without adding lane-specific branches.

**Legality and limitations**

- The loop must have a header and one body/latch block, an invariant bound, supported induction, and uniform exception-region membership.
- Storage comes from recognized arrays, strings, or stable by-value span views. Raw pointers with unknown extents remain scalar.
- Every memory access needs a static bound proof or a runtime array extent guard. Unknown aliasing, offset dependencies, volatile accesses, and potentially overlapping views prevent vectorization.
- Strings are read-only. Same-buffer pointwise updates are supported when each iteration touches only its own element.
- Mixed scalar widths remain unsupported. Implicit promotion of byte or short arithmetic can therefore prevent vectorization.
- Floating-point reductions remain scalar because reassociation changes rounding. Floating-point `Min`/`Max`, integer `Abs`, checked arithmetic, and unsupported calls retain their scalar behavior, including exceptions and completed stores.
- The scalar tail handles empty, short, and non-multiple lengths. Integer trip-count calculations use widened arithmetic to avoid overflow at the vector-loop boundary.

## Pre-size Lists / Array Builder Opts

This pass provides two related transformations:

- Pre-size confined lists when a loop's trip count can be proved without adding observable collection operations.
- Replace a list used only as an array builder with direct array writes when its final length and allocation placement are proved.

### Example

A fixed array source has a trustworthy cardinality:

```cs
public static int[] DynamicList_Enumer(int[] items)
{
    var list = new List<int>();

    for (int i = 0; i < items.Length; i++) {
        list.Add(items[i] * 3 / 5 + 1);
    }

    return list.ToArray();
}
```

An eligible array-builder loop can write directly to its result:

```cs
public static int[] DynamicList_Enumer(int[] items)
{
    var result = new int[items.Length];

    for (int i = 0; i < items.Length; i++) {
        result[i] = items[i] * 3 / 5 + 1;
    }

    return result;
}
```

An `IReadOnlyList<T>` parameter alone does not establish the same contract: its `Count` getter and enumerator may have independent observable behavior. Such a source retains enumeration unless the optimizer proves a stronger concrete contract.

**Limitations and constraints**

- The target list must be a recognized fresh allocation that does not escape or expose its capacity.
- Direct array construction requires unconditional appends, one final `ToArray()`, and no other uses that observe intermediate list contents.
- Conditional appends can prevent exact array construction even when a safe initial capacity is available.
- Capacity multiplication uses widened arithmetic. An unrepresentable capacity estimate does not become a wrapped allocation size.
- Allocation placement must preserve exception-region behavior and dominate all relevant uses.
- General struct enumerators, spans, and immutable-array builders are not recognized as list-presizing sources merely because they expose a length.

### Benchmarks

[Source](../tests/Benchmarks/ListArrayBuilding.cs). `DirectArray_Gen` provides a direct-allocation reference; `DynamicList_Gen` exercises a counting loop; `DynamicList_Enumer` intentionally enumerates through `IReadOnlyList<int>`.

| Method             | Toolchain          | Count |         Mean |      Error |     StdDev | Ratio | RatioSD |   Gen0 |   Gen1 | Allocated | Alloc Ratio |
| ------------------ | ------------------ | ----- | -----------: | ---------: | ---------: | ----: | ------: | -----: | -----: | --------: | ----------: |
| DirectArray_Gen    | .NET 10.0          | 4     |     4.302 ns |  0.1499 ns |  0.0992 ns |  1.00 |    0.03 | 0.0025 |      - |      40 B |        1.00 |
| DirectArray_Gen    | .NET 10.0 + Cilica | 4     |     4.123 ns |  0.1141 ns |  0.0755 ns |  0.96 |    0.03 | 0.0025 |      - |      40 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DirectArray_Gen    | .NET 10.0          | 1024  | 2,991.820 ns | 23.7106 ns | 14.1098 ns |  1.00 |    0.01 | 0.2544 |      - |    4120 B |        1.00 |
| DirectArray_Gen    | .NET 10.0 + Cilica | 1024  | 3,013.001 ns | 32.4651 ns | 21.4736 ns |  1.01 |    0.01 | 0.2612 |      - |    4120 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Gen    | .NET 10.0          | 4     |    12.659 ns |  0.3230 ns |  0.2136 ns |  1.00 |    0.02 | 0.0071 |      - |     112 B |        1.00 |
| DynamicList_Gen    | .NET 10.0 + Cilica | 4     |     4.240 ns |  0.1265 ns |  0.0837 ns |  0.34 |    0.01 | 0.0025 |      - |      40 B |        0.36 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Gen    | .NET 10.0          | 1024  | 3,259.718 ns | 38.5166 ns | 25.4763 ns |  1.00 |    0.01 | 0.7922 |      - |   12544 B |        1.00 |
| DynamicList_Gen    | .NET 10.0 + Cilica | 1024  | 3,012.050 ns | 34.3305 ns | 22.7075 ns |  0.92 |    0.01 | 0.2546 |      - |    4120 B |        0.33 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Enumer | .NET 10.0          | 4     |    12.827 ns |  0.1651 ns |  0.0982 ns |  1.00 |    0.01 | 0.0071 |      - |     112 B |        1.00 |
| DynamicList_Enumer | .NET 10.0 + Cilica | 4     |    12.680 ns |  0.2571 ns |  0.1701 ns |  0.99 |    0.01 | 0.0071 |      - |     112 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Enumer | .NET 10.0          | 1024  | 1,323.260 ns | 13.2535 ns |  7.8869 ns |  1.00 |    0.01 | 0.7948 | 0.0132 |   12544 B |        1.00 |
| DynamicList_Enumer | .NET 10.0 + Cilica | 1024  | 1,351.125 ns | 17.7084 ns | 11.7130 ns |  1.02 |    0.01 | 0.7959 | 0.0134 |   12544 B |        1.00 |

## Scalar Replacement

Removes eligible non-escaping object allocations by replacing their fields with local values or slots. Constructor effects, type initialization, finalization, identity observations, and address escapes constrain eligibility. SSA promotion and simplification then remove redundant local loads and stores.

### Example

```cs
public static int SROA_ClassEnumerator(int[] array)
{
    var iterator = new ArrayEnumerator(array);
    int sum = 0;

    foreach (int value in iterator) {
        sum += value * 2 + 1;
    }

    return sum;
}

sealed class ArrayEnumerator(int[] array, int index = 0)
{
    public int Current { get; private set; }

    public bool MoveNext()
    {
        if (index < array.Length) {
            Current = array[index++];
            return true;
        }

        return false;
    }

    public ArrayEnumerator GetEnumerator() => this;
}
```

After eligible calls and fields are exposed, the allocation can disappear:

```cs
public static int SROA_ClassEnumerator(int[] array)
{
    int sum = 0;

    for (int index = 0; index < array.Length; index++) {
        sum += array[index] * 2 + 1;
    }

    return sum;
}
```

The sealed enumerator has no disposal contract. An enumerator with observable `Dispose()` behavior retains that behavior and its exception scope. Pinned, exposed, and exception-sensitive locals retain storage, and representation-changing stores preserve truncation and rounding. Constructor expansion follows the [inlining size policy](#inlining-policy), so an eligible allocation can remain when expanding its constructor would exceed the caller's budget.

## Reflection Folding

Statically known reflection accesses can become field loads, field stores, method calls, delegate construction, and function-pointer loads. Property accesses call their getters or setters. The default pipeline applies reflection folding before inlining and revisits accesses after inlining exposes constant types and member names.

The pass recognizes known `typeof` values and exact-type `GetType()` results, followed by `GetField`, `GetMethod`, or `GetProperty` with a constant name and optional constant binding flags. Constructed types such as `typeof(Container<T>)` are supported inside generic methods. Supported flags are `Public`, `NonPublic`, `Instance`, `Static`, and `DeclaredOnly`. A lookup must identify one declared member or a virtual override of an inherited slot. Other inherited members and name collisions remain reflective unless `DeclaredOnly` excludes the base members.

Non-public members and declaring types are eligible under the same access policy as inlining. When optimizing CoreLib itself, accesses that would require bypassing member visibility remain reflective.

| Operation | Eligible accesses |
| --- | --- |
| `FieldInfo.GetValue` | Accessible fields whose values can be boxed or returned as object references |
| `FieldInfo.SetValue` | Mutable, accessible fields with compatible reference values or exactly typed boxed values; 64-bit numeric and struct stores remain reflective |
| `MethodInfo.Invoke` | Nongeneric or constructed generic methods with compatible arguments supplied through `null`, `Array.Empty<object>()`, or a local argument array whose contents are known at invocation |
| `PropertyInfo.GetValue` / `SetValue` | Non-indexed properties with an accessible accessor and compatible values |
| `MethodInfo.CreateDelegate` / `Delegate.CreateDelegate` | Known delegate types and methods with matching signatures, including generic `CreateDelegate<T>()` overloads and the `MethodInfo`-based static factory overloads |
| `MethodInfo.MethodHandle` / `RuntimeMethodHandle.GetFunctionPointer` | Known static methods; pointer lookup requires a local handle whose value is established and cannot be changed through an alias |
| `MethodInfo.MakeGenericMethod` / `Type.MakeGenericType` | Known definitions and type arguments whose generic constraints can be established; successful constructions can feed member lookup, invocation, delegate creation, and function-pointer folding |

Generic arguments can be constants or type parameters such as `typeof(T)` from the containing method or type. Their declared constraints must guarantee that each construction is valid. Type arguments and definitions exposed by inlining participate in subsequent folding passes. Proven type constructions can become `typeof` expressions even when their `Type` results remain in use. Invalid, ambiguous, or unproven constructions retain runtime validation. Generic method-construction calls disappear when their consumers fold and their metadata is otherwise unused. A bare `typeof(T)` does not identify members when the concrete type is unknown.

Instance accesses require a receiver proven non-null and compatible with the declaring type. Boxed struct receivers retain their identity and mutations. Method calls preserve virtual dispatch, boxed results, and `TargetInvocationException` wrapping; the full `Invoke` overload also supports `BindingFlags.DoNotWrapExceptions` with null binder and culture arguments. Invocations requiring wrapping remain reflective inside exception filters or when the caller's non-Exception wrapping policy differs from the runtime library's policy. Reflective invocation with unsupported conversions, possible `Type.Missing` arguments, custom binders, `ref`/`out` parameters, or unresolved receivers retains its runtime calls.

Delegate folding supports open static and instance methods, closed instance methods with proven non-null receivers, and static methods bound to a reference-type first argument, including null. Parameter and return signatures must match exactly; compatible `ref` parameters and returns are supported. Virtual and interface calls retain their dispatch behavior, including generic virtual methods. Boxed receivers preserve their identity, mutations, and escaping managed references. Delegate-type initialization and both declaring-type and method instantiations are preserved. Closed virtual factories retain reflection when the delegate can escape or its identity can be observed and dispatch selects a different or unproven target. This preserves `Delegate.Method` and equality behavior. Closed null instance targets, open delegates to abstract methods, open generic virtual delegates on reference types, signature variance, and name-based factory overloads retain runtime handling. Subsequent optimizations can turn eligible delegate invocations into direct calls.

Function-pointer folding preserves managed calling conventions and unmanaged entry points for methods marked `UnmanagedCallersOnly`. Instance method handles, generic method definitions, and pointer lookups through reassigned or exposed local handles remain reflective. These pointers retain the initialization behavior of taking a method address; target type initialization occurs when required by invocation.

Generic field loads and boxed value-type arguments are supported when their types are established. Generic field stores require a reference-type constraint when the storage width is unknown. Generic invocation arguments that could contain `Type.Missing` retain reflection's default-argument handling.

Unused member-discovery calls are removed when their successful lookup is established. Discovery remains when other code observes the metadata or `CompilationSettings.PreserveTrimmingDependencies` is enabled. A `GetType()` call on a potentially null receiver retains its null check even when a subsequent static access folds.

## Algebraic Simplification

Simplifies fixed-width 32-bit and 64-bit integer expressions using common factors and constant contributions.

For example, `x * a + x * b` can reduce to `x * (a + b)`, and `x * a - x` can reduce to `x * (a - 1)`.

### Bitwise expressions

Simplifies 32-bit and 64-bit integer expressions with common operands and constant masks. Conditional single-bit sets, clears, and toggles can become direct bitwise operations, including inverted tests and tests of the sign bit. Repeated masked contributions from the same value can combine into one mask.

For example, these conditional flag updates:

```cs
if ((flags & 16) != 0) value |= 16;
if ((flags & 256) != 0) value |= 256;
```

can reduce to:

```cs
value |= flags & 272;
```

Common operands also simplify expressions such as `(x & a) | (x & b)` into `x & (a | b)`, while constant contributions can simplify `(x | 1) & 2` into `x & 2`.

## Common Sub-expression Elimination

CSE uses dominator-based global value numbering. It reuses dominating expressions only when their operands and exception region remain valid. Memory accesses use alias information and an index of potential writes and ordering barriers, including writes reached through loop backedges.

### Example

```cs
public static int SumTwice(int[] values, int index) => values[index] + values[index];
```

With no intervening write or barrier, one load can supply both operands:

```cs
public static int SumTwice(int[] values, int index)
{
    int value = values[index];
    return value + value;
}
```

Supported known-call contracts include selected dictionary lookups when comparer and mutation behavior are proved. Unknown calls, culture-sensitive operations, allocation identity, exceptions, ordering, and possible nontermination remain conservative. Dead-code elimination does not remove a call merely because its return value is unused.

## Assertion Propagation

Uses facts established by earlier branches and successful dereferences to simplify later comparisons and remove redundant null checks. A fact applies only on paths where it is guaranteed to hold. References merged from different branches can retain nullness facts shared by all incoming paths.

### Example

The `IndexOf()` call checks `text` for null, so its normal continuation does not need another null test:

```cs
[Optimize]
public static int FindAsciiA(string text)
{
    int index = text.IndexOf('A');
    return text != null ? index : -1;
}
```

The redundant condition can disappear:

```cs
public static int FindAsciiA(string text)
    => text.IndexOf('A');
```

## Devirtualization

Devirtualization replaces virtual and interface dispatch with direct calls when Cilica can determine the target. It supports class overrides, instance and static interface members, and delegates with known targets. Resolving a target can also make the call eligible for inlining.

### Example

Although `reader` is declared as an interface, its concrete type is known:

```cs
interface IReader
{
    int Read();
}

sealed class ConstantReader : IReader
{
    public int Read() => 42;
}

[Optimize]
public static int ReadValue()
{
    IReader reader = new ConstantReader();
    return reader.Read();
}
```

Cilica can replace the interface dispatch with a direct call to `ConstantReader.Read()`:

```cs
public static int ReadValue()
{
    var reader = new ConstantReader();
    return reader.Read();
}
```

Dispatch can be resolved even when the target is marked `NoInlining` or method inlining is disabled. When inlining and scalar replacement are also eligible, this example can reduce to `return 42;`.

## Method Inlining

Inlining exposes constants, fields, and loop bodies across method boundaries. It works together with devirtualization and simplification: inlining a factory can reveal a concrete receiver, resolving that receiver can expose another method body, and simplifying that body can enable further inlining. The process can continue through factory chains and calls to known delegate targets while useful work and budget remain.

### Example

Using the reader types from the [devirtualization example](#devirtualization), a helper can hide the concrete receiver from the caller:

```cs
static IReader CreateReader() => new ConstantReader();

[Optimize]
public static int ReadFromFactory() => CreateReader().Read();
```

Inlining `CreateReader()` exposes `ConstantReader`, allowing the `Read()` call to resolve and inline. With the unused allocation removed by scalar replacement, the result is:

```cs
public static int ReadFromFactory() => 42;
```

### Inlining policy

`NoInlining` prevents a method from being inlined. `AggressiveInlining` encourages inlining but does not guarantee it.

The optimizer API exposes these controls:

- `CompilationSettings.EnableInlining` controls method inlining and constructor expansion, including scalar replacement. It is enabled by default.
- `CompilationSettings.AllowCrossAssemblyIPO` permits optimizations across assembly boundaries. It is enabled by default.
- `CompilationSettings.MaxInlinedMethodILSize` limits caller IL growth during method inlining and constructor expansion, with a default of 32 KiB. Methods already above the limit can still accept inlining that does not increase their size.

## Pipeline and Compiler Benchmarks

The default pipeline runs CFG simplification, SSA promotion, LINQ expansion, instruction simplification, devirtualization, inlining, dead-code elimination, scalar replacement, value numbering, assertion propagation, list presizing, loop strength reduction, and loop vectorization.

The optimizer runs with strict failure handling and per-pass verification before the optimized executable is measured. Compiler benchmarks are selected separately:

```sh
dotnet run --project tests/Benchmarks -c Release -- --filter '*'
dotnet run --project tests/Benchmarks -c Release -- --compiler --filter '*RepeatedLoads*'
```

`PassTimingInspector` reports invocations, changes, failures, elapsed time, allocations, and analysis-cache activity.
