# Optimization Passes

- [LINQ Expansion](#linq-expansion)
- [Loop Strength Reduction](#loop-strength-reduction)
- [Loop Vectorization](#loop-vectorization)
- [Pre-size Lists / Array Builder Opts](#pre-size-lists--array-builder-opts)
- [Scalar Replacement](#scalar-replacement)
- [Common Sub-expression Elimination](#common-sub-expression-elimination)
- [Method Inlining](#method-inlining)
- [Pipeline and Compiler Benchmarks](#pipeline-and-compiler-benchmarks)

DistIL also implements canonicalization, SSA promotion, control-flow simplification, and dead-code elimination. This list describes the larger transformations. The default pipeline is defined by `CompilationPipeline.CreateDefault()`.

Code examples show source and representative equivalent output, with compiler-generated delegate setup and temporary names simplified for readability. Decompiled unsafe operations correspond to lifted IL instructions; exact output depends on the surrounding method and subsequent simplification.

The benchmark tables use .NET 10.0.14, BenchmarkDotNet 0.15.8, and an Intel Core i7-13700K on Windows 11 x64. Each job uses one launch, three warmup iterations, and ten measured iterations targeting 200 ms each. Error is half the 99.9% confidence interval; ratios compare DistIL with the baseline for the same workload. The tables include both improvements and regressions.

## LINQ Expansion

Rewrites recognized LINQ queries into imperative code in bottom-up order. Pattern matching identifies supported sources, intermediate stages, and consuming operations. Compatible stages fuse into a loop, while operations with unproven collection semantics remain library calls.

### Example

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

### Benchmarks

[Source](/tests/Benchmarks/LinqExpansion.cs). These workloads include mutable-list queries, a string predicate count, and a LINQ ray tracer. Their results measure the complete default pipeline together with the runtime JIT, rather than one isolated pass.

| Method         | Toolchain          | ElemCount | Mean         | Error       | StdDev      | Ratio | RatioSD |
|--------------- |------------------- |---------- |-------------:|------------:|------------:|------:|--------:|
| FilterObjects  | .NET 10.0          | 4096      |    14.406 μs |   0.6405 μs |   0.3350 μs |  1.00 |    0.03 |
| FilterObjects  | .NET 10.0 + DistIL | 4096      |     9.988 μs |   0.1973 μs |   0.1305 μs |  0.69 |    0.02 |
|                |                    |           |              |             |             |       |         |
| FirstPredicate | .NET 10.0          | 4096      |     6.768 μs |   0.0661 μs |   0.0437 μs |  1.00 |    0.01 |
| FirstPredicate | .NET 10.0 + DistIL | 4096      |     7.189 μs |   0.1188 μs |   0.0707 μs |  1.06 |    0.01 |
|                |                    |           |              |             |             |       |         |
| Aggregate      | .NET 10.0          | 4096      |    30.469 μs |   0.7975 μs |   0.5275 μs |  1.00 |    0.02 |
| Aggregate      | .NET 10.0 + DistIL | 4096      |    13.313 μs |   0.6233 μs |   0.4123 μs |  0.44 |    0.01 |
|                |                    |           |              |             |             |       |         |
| CountLetters   | .NET 10.0          | 4096      |     3.285 μs |   0.0473 μs |   0.0313 μs |  1.00 |    0.01 |
| CountLetters   | .NET 10.0 + DistIL | 4096      |     2.492 μs |   0.0079 μs |   0.0047 μs |  0.76 |    0.01 |
|                |                    |           |              |             |             |       |         |
| RayTracer      | .NET 10.0          | 4096      | 8,980.562 μs | 188.9555 μs | 112.4444 μs |  1.00 |    0.02 |
| RayTracer      | .NET 10.0 + DistIL | 4096      | 5,288.014 μs | 112.4827 μs |  74.4003 μs |  0.59 |    0.01 |

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

The pass preserves covariant array-store checks and declines loops whose span view may be reassigned during iteration. It accesses arrays through `MemoryMarshal.GetArrayDataReference()` and spans through their public reference accessors. Loop analysis tracks nesting and combines backedges sharing a header.

## Loop Vectorization

Loop vectorization is enabled in the default pipeline. Eligible innermost loops use `Vector256<T>` operations, a runtime hardware-acceleration check, and a scalar tail. The scalar loop also handles execution when vector acceleration is unavailable.

### Examples

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
    var target = Vector256.Create((ushort)'A');

    if (Vector256.IsHardwareAccelerated) {
        for (; (long)text.Length - index >= 16; index += 16) {
            ref char current = ref Unsafe.Add(ref first, index);
            var values = Unsafe.ReadUnaligned<Vector256<ushort>>(ref Unsafe.As<char, byte>(ref current));
            var matches = Vector256.Equals(values, target);
            count += BitOperations.PopCount(matches.AsByte().ExtractMostSignificantBits()) >> 1;
        }
    }

    for (; index < text.Length; index++) {
        count += Unsafe.Add(ref first, index) == 'A' ? 1 : 0;
    }

    return count;
}
```

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
    var offsets = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
    var scale = Vector256.Create(3);
    var biasVector = Vector256.Create(bias);

    if (Vector256.IsHardwareAccelerated) {
        for (; (long)destination.Length - index >= 8; index += 8) {
            var indices = Vector256.Create(index) + offsets;
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

### Benchmarks

[Source](/tests/Benchmarks/AutoVectorization.cs). The suite includes both eligible loops and kernels that exercise scalar fallback. `GenerateInts` contains potentially overflowing integer `Abs`; `CountLetters` includes promoted character-range arithmetic. Those operations can prevent vectorization. `NormalizeFloats` retains its scalar floating-point min/max reduction, while its pointwise normalization loop is eligible for vectorization.

| Method          | Toolchain          | ElemCount | Mean        | Error     | StdDev   | Ratio | Code Size |
|---------------- |------------------- |---------- |------------:|----------:|---------:|------:|----------:|
| CountLetters    | .NET 10.0          | 4096      |  3,255.1 ns |  25.59 ns | 16.93 ns |  1.00 |     450 B |
| CountLetters    | .NET 10.0 + DistIL | 4096      |  2,497.1 ns |  27.23 ns | 18.01 ns |  0.77 |     211 B |
|                 |                    |           |             |           |          |       |           |
| GenerateInts    | .NET 10.0          | 4096      |  1,182.1 ns |   8.12 ns |  4.25 ns |  1.00 |      84 B |
| GenerateInts    | .NET 10.0 + DistIL | 4096      |  1,475.8 ns |   7.98 ns |  5.28 ns |  1.25 |      88 B |
|                 |                    |           |             |           |          |       |           |
| GenerateFloats  | .NET 10.0          | 4096      |  2,783.8 ns |  33.88 ns | 22.41 ns |  1.00 |   1,724 B |
| GenerateFloats  | .NET 10.0 + DistIL | 4096      |    605.2 ns |   5.90 ns |  3.90 ns |  0.22 |   1,861 B |
|                 |                    |           |             |           |          |       |           |
| NormalizeFloats | .NET 10.0          | 4096      | 11,249.7 ns | 101.59 ns | 60.46 ns |  1.00 |   1,798 B |
| NormalizeFloats | .NET 10.0 + DistIL | 4096      | 10,585.1 ns | 116.04 ns | 76.75 ns |  0.94 |   1,868 B |

### Features

**Supported operations**

- Memory: nonvolatile, unit-stride loads and stores with proven storage extents. Addresses use either an invariant base plus the counter or a sequential pointer counter.
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
- Every memory access needs a bound proof. Unknown aliasing, offset dependencies, volatile accesses, and potentially overlapping views prevent vectorization.
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

[Source](/tests/Benchmarks/ListArrayBuilding.cs). `DirectArray_Gen` provides a direct-allocation reference; `DynamicList_Gen` exercises a counting loop; `DynamicList_Enumer` intentionally enumerates through `IReadOnlyList<int>`.

| Method             | Toolchain          | Count | Mean         | Error      | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------------- |------ |-------------:|-----------:|-----------:|------:|--------:|-------:|-------:|----------:|------------:|
| DirectArray_Gen    | .NET 10.0          | 4     |     4.409 ns |  0.2437 ns |  0.1612 ns |  1.00 |    0.05 | 0.0025 |      - |      40 B |        1.00 |
| DirectArray_Gen    | .NET 10.0 + DistIL | 4     |     3.989 ns |  0.1627 ns |  0.1076 ns |  0.91 |    0.04 | 0.0025 |      - |      40 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DirectArray_Gen    | .NET 10.0          | 1024  | 3,009.692 ns | 19.4498 ns | 11.5743 ns |  1.00 |    0.01 | 0.2566 |      - |    4120 B |        1.00 |
| DirectArray_Gen    | .NET 10.0 + DistIL | 1024  | 3,007.700 ns | 30.9451 ns | 20.4683 ns |  1.00 |    0.01 | 0.2517 |      - |    4120 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Gen    | .NET 10.0          | 4     |    12.904 ns |  0.8910 ns |  0.5893 ns |  1.00 |    0.06 | 0.0071 |      - |     112 B |        1.00 |
| DynamicList_Gen    | .NET 10.0 + DistIL | 4     |     4.260 ns |  0.1754 ns |  0.1160 ns |  0.33 |    0.02 | 0.0025 |      - |      40 B |        0.36 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Gen    | .NET 10.0          | 1024  | 3,322.325 ns | 91.1160 ns | 60.2676 ns |  1.00 |    0.02 | 0.7990 |      - |   12544 B |        1.00 |
| DynamicList_Gen    | .NET 10.0 + DistIL | 1024  | 2,991.996 ns | 27.5023 ns | 18.1911 ns |  0.90 |    0.02 | 0.2561 |      - |    4120 B |        0.33 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Enumer | .NET 10.0          | 4     |    12.943 ns |  0.3778 ns |  0.2248 ns |  1.00 |    0.02 | 0.0071 |      - |     112 B |        1.00 |
| DynamicList_Enumer | .NET 10.0 + DistIL | 4     |    12.582 ns |  0.2776 ns |  0.1836 ns |  0.97 |    0.02 | 0.0071 |      - |     112 B |        1.00 |
|                    |                    |       |              |            |            |       |         |        |        |           |             |
| DynamicList_Enumer | .NET 10.0          | 1024  | 1,330.843 ns | 17.5525 ns | 11.6099 ns |  1.00 |    0.01 | 0.7997 | 0.0133 |   12544 B |        1.00 |
| DynamicList_Enumer | .NET 10.0 + DistIL | 1024  | 1,319.549 ns | 20.9208 ns | 10.9420 ns |  0.99 |    0.01 | 0.7957 | 0.0130 |   12544 B |        1.00 |

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

The sealed enumerator has no disposal contract. An enumerator with observable `Dispose()` behavior retains that behavior and its exception scope. Pinned, exposed, and exception-sensitive locals retain storage, and representation-changing stores preserve truncation and rounding.

## Common Sub-expression Elimination

CSE uses dominator-based global value numbering. It reuses dominating expressions only when their operands and exception region remain valid. Memory accesses use alias information and an index of potential writes and ordering barriers, including writes reached through loop backedges.

### Example

```cs
public static int SumTwice(int[] values, int index)
    => values[index] + values[index];
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

## Method Inlining

Inlining combines call-site benefits with callee costs. Direct calls and resolved virtual slots can expose constants, fields, and loop bodies to later passes. Callees are normalized and verified, and failed compilation results are not reused for inlining.

The default policy uses an initial budget of 80 plus half the caller's estimated cost, a per-call cost threshold of 250, and independent limits of 16 ancestor levels, 128 expansions, and 4096 expanded IR instructions. Ordinary IL candidates above 256 instructions are rejected early; `AggressiveInlining` raises that early limit to 4096. `NoInlining`, unsupported targets, and recursive ancestor chains prevent expansion.

Virtual-call null checks and required type initialization are preserved. Cross-assembly IPO is controlled by compilation settings. When imported code requires access to private members, the compilation adds `IgnoresAccessChecksToAttribute` metadata as needed and removes unused auxiliary artifacts.

## Pipeline and Compiler Benchmarks

The default pipeline runs CFG simplification, SSA promotion, LINQ expansion, instruction simplification, inlining, dead-code elimination, scalar replacement, value numbering, list presizing, loop strength reduction, and loop vectorization. Bounded simplification iterations follow transformations that expose more work. `AssertionProp` and `ExtractThrows` are also available for custom pipelines.

Runtime benchmark pairs compare .NET 10 with the same runtime plus DistIL. The optimizer runs with strict failure handling and per-pass verification before the optimized executable is measured. Compiler benchmarks are selected separately:

```sh
dotnet run --project tests/Benchmarks -c Release -- --filter '*'
dotnet run --project tests/Benchmarks -c Release -- --compiler --filter '*RepeatedLoads*'
```

To use the measurement settings shown in the tables, add `--iterationCount 10 --warmupCount 3 --iterationTime 200 --launchCount 1` to the runtime benchmark command.

`PassTimingInspector` reports invocations, changes, failures, elapsed time, allocations, and analysis-cache activity. The repeated-load benchmark measures compiler work in value numbering; it is separate from runtime application throughput. Measurements depend on the machine, runtime, workload, and enabled inspections, and are not correctness thresholds.
