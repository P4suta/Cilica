namespace DistIL.Tests.Passes;

using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;

[Collection("ModuleResolver")]
public class LoopVectorizerTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EqualLengthArrayMapsVectorizeWithAliasingAndOrderedStores(bool negateGuard, bool swapOperands)
    {
        var body = CreateGuardedArrayMap(negateGuard, swapOperands);
        Vectorize(body, expected: true);
        var run = Utils.Compile<Action<int[], int[], bool>>(body);
        foreach (int count in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 33 }) {
            var original = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? int.MaxValue : i).ToArray();
            var source = (int[])original.Clone();
            var destination = new int[count];
            run(source, destination, false);
            Assert.Equal(original.Select(x => unchecked(x * 3 + 7)), destination);
            Assert.Equal(original.Select(x => unchecked(x * 3 + 8)), source);

            source = (int[])original.Clone();
            run(source, source, false);
            Assert.Equal(original.Select(x => unchecked(x * 3 + 8)), source);
        }
        var shortDestination = new int[1];
        run([1, 2], shortDestination, false);
        Assert.Equal(0, shortDestination[0]);
    }

    [Fact]
    public void BypassedLengthGuardsDoNotProveBounds()
    {
        var body = CreateGuardedArrayMap(negateGuard: false, swapOperands: false, bypassGuard: true);
        Vectorize(body, expected: false);
        var run = Utils.Compile<Action<int[], int[], bool>>(body);
        int[] source = [1, 2, 3];
        var destination = new int[1];
        Assert.Throws<IndexOutOfRangeException>(() => run(source, destination, true));
        Assert.Equal(new[] { 10 }, destination);
        Assert.Equal(new[] { 11, 2, 3 }, source);
    }

    [Fact]
    public void GuardedCountParametersCanBoundArrayStorage()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray(), PrimType.Int32);
        var entry = body.CreateBlock();
        var preheader = body.CreateBlock();
        var exit = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var length = builder.CreateConvert(builder.CreateArrayLen(body.Args[0]), PrimType.Int32);
        entry.SetBranch(new BranchInst(builder.CreateEq(body.Args[1], length), preheader, exit));
        var loop = new LoopBuilder(preheader);
        preheader.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        loop.Build(header => header.CreateSlt(index, body.Args[1]), items => {
            items.CreateArrayStore(body.Args[0], index, ConstInt.CreateI(7));
        });
        loop.Exit.CreateReturn();
        exit.SetBranch(new ReturnInst());
        Vectorize(body, expected: true);
        var run = Utils.Compile<Action<int[], int>>(body);
        var values = new int[33];
        run(values, 33);
        Assert.All(values, value => Assert.Equal(7, value));
        run([], 0);
        run(values, -1);
        Assert.All(values, value => Assert.Equal(7, value));
    }

    private MethodBody CreateGuardedArrayMap(bool negateGuard, bool swapOperands, bool bypassGuard = false)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray(), PrimType.Int32.CreateArray(), PrimType.Bool);
        var entry = body.CreateBlock();
        var guard = bypassGuard ? body.CreateBlock() : entry;
        var preheader = body.CreateBlock();
        var exit = body.CreateBlock();
        if (bypassGuard) entry.SetBranch(new BranchInst(body.Args[2], preheader, guard));
        var builder = new IRBuilder(guard);
        var sourceLength = builder.CreateConvert(builder.CreateArrayLen(body.Args[0]), PrimType.Int32);
        var destinationLength = builder.CreateConvert(builder.CreateArrayLen(body.Args[1]), PrimType.Int32);
        var condition = builder.CreateCmp(negateGuard ? CompareOp.Ne : CompareOp.Eq,
            swapOperands ? destinationLength : sourceLength, swapOperands ? sourceLength : destinationLength);
        guard.SetBranch(new BranchInst(condition, negateGuard ? exit : preheader, negateGuard ? preheader : exit));
        var loop = new LoopBuilder(preheader);
        preheader.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), items => {
            var value = items.CreateArrayLoad(body.Args[0], index);
            items.CreateArrayStore(body.Args[1], index, items.CreateAdd(items.CreateMul(value, ConstInt.CreateI(3)), ConstInt.CreateI(7)));
            // A load following a store must observe it, including when the arrays alias.
            items.CreateArrayStore(body.Args[0], index, items.CreateAdd(items.CreateArrayLoad(body.Args[1], index), ConstInt.CreateI(1)));
        });
        loop.Exit.CreateReturn();
        exit.SetBranch(new ReturnInst());
        return body;
    }

    [Fact]
    public void UnsignedReductionsCanConsumeSignedLanesOfTheSameWidth()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.UInt64, PrimType.Int64.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value result = null!;
        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var item = builder.CreateArrayLoad(body.Args[0], index);
            result = loop.CreateAccum(ConstInt.Create(PrimType.UInt64, 1), current => builder.CreateMul(current, item));
        });
        loop.Exit.CreateReturn(result);
        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<long[], ulong>>(body);
        foreach (int length in new[] { 0, 1, 4, 5, 17 }) {
            var values = Enumerable.Range(0, length).Select(i => i % 2 == 0 ? -3L : 7L).ToArray();
            ulong expected = 1;
            foreach (long item in values) expected = unchecked(expected * (ulong)item);
            Assert.Equal(expected, run(values));
        }
    }

    [Fact]
    public void IntegerArrayLoopsVectorizeWithScalarTails()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var value = builder.CreateAdd(builder.CreateMul(index, ConstInt.CreateI(3)), ConstInt.CreateI(7));
            builder.CreateArrayStore(body.Args[0], index, value);
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: true);
        var run = Utils.Compile<Action<int[]>>(body);

        foreach (int count in new[] { 0, 1, 7, 8, 9, 16, 31, 33 }) {
            var values = new int[count];
            run(values);

            Assert.Equal(Enumerable.Range(0, count).Select(i => i * 3 + 7), values);
        }
    }

    [Fact]
    public void DefaultPipelineVectorizesWithoutAnAttributeFlag()
    {
        var body = CreateReduction(PrimType.Int32, ConstInt.CreateI(0), BinaryOp.Add);
        var definition = body.Definition;
        var compilation = new Compilation(definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        var result = CompilationPipeline.CreateDefault(compilation).Run([definition]);
        result.ThrowIfFailed();

        Assert.Contains(definition.Body!.Instructions().OfType<CallInst>(), call => call.Method.DeclaringType.Name.StartsWith("Vector256"));
        Assert.Equal(36, Utils.Compile<Func<int[], int>>(definition.Body)([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void PredicatedCountsUseBooleanMasks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Bool);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value result = null!;

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var item = builder.CreateArrayLoad(body.Args[0], index);
            var condition = builder.CreateAnd(body.Args[1], builder.CreateEq(item, ConstInt.CreateI(7)));
            result = loop.CreateAccum(ConstInt.CreateI(5), current => builder.CreateAdd(current, condition));
        });
        loop.Exit.CreateReturn(result);

        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<int[], bool, int>>(body);
        int[] values = Enumerable.Range(0, 35).Select(index => index % 3 == 0 ? 7 : 9).ToArray();

        Assert.Equal(17, run(values, true));
        Assert.Equal(5, run(values, false));
    }

    [Fact]
    public void PointwiseFloatNegationPreservesSignedZero()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Single.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var value = builder.CreateArrayLoad(body.Args[0], index);
            builder.CreateArrayStore(body.Args[0], index, builder.Emit(new UnaryInst(UnaryOp.FNeg, value)));
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: true);
        float[] values = [0.0f, -0.0f, 1, -1, float.PositiveInfinity, float.NegativeInfinity, float.NaN, -3, -0.0f];
        var expected = values.Select(value => BitConverter.SingleToInt32Bits(-value)).ToArray();
        Utils.Compile<Action<float[]>>(body)(values);

        Assert.Equal(expected, values.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void IntegerInductionConvertsToFloatLanes()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Single.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            builder.CreateArrayStore(body.Args[0], index, builder.CreateConvert(index, PrimType.Single));
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: true);
        var values = new float[35];
        Utils.Compile<Action<float[]>>(body)(values);

        Assert.Equal(Enumerable.Range(0, values.Length).Select(value => (float)value), values);
    }

    [Fact]
    public void ByValueSpansHaveStableViewMetadata()
    {
        var spanType = _modResolver.Import(typeof(Span<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, spanType);
        var entry = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var span = body.CreateVar(spanType);
        builder.CreateStore(span, body.Args[0]);
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateCallVirt("get_Length", span)), items => {
            var address = items.CreateCallVirt("get_Item", span, index);
            items.CreateStore(address, items.CreateMul(index, ConstInt.CreateI(3)));
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: true);
        var values = new int[35];
        Utils.Compile<SpanAction>(body)(values);

        Assert.Equal(Enumerable.Range(0, values.Length).Select(value => value * 3), values);
    }

    delegate void SpanAction(Span<int> values);

    [Fact]
    public void NegativeTripCountsDoNotOverflowTheVectorBound()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value result = null!;

        loop.Build(header => header.CreateSlt(index, body.Args[0]), builder => {
            result = loop.CreateAccum(ConstInt.CreateI(9), current => builder.CreateAdd(current, index));
        });
        loop.Exit.CreateReturn(result);

        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<int, int>>(body);

        Assert.Equal(9, run(int.MinValue));
        Assert.Equal(9, run(-1));
        Assert.Equal(9, run(0));
        Assert.Equal(9 + 32 * 33 / 2, run(33));
    }

    [Fact]
    public void VolatileLoadsRemainScalar()
    {
        var body = CreateReduction(PrimType.Int32, ConstInt.CreateI(0), BinaryOp.Add);
        Assert.Single(body.Instructions().OfType<LoadInst>()).Flags = PointerFlags.Volatile;

        Vectorize(body, expected: false);

        Assert.Single(body.Instructions().OfType<LoadInst>().Where(load => load.IsVolatile));
    }

    [Fact]
    public void IntegerReductionsPreserveSeedsAndWrapping()
    {
        var body = CreateReduction(PrimType.Int32, ConstInt.CreateI(17), BinaryOp.Add);
        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<int[], int>>(body);

        foreach (int count in new[] { 0, 1, 7, 8, 15, 16, 33 }) {
            var values = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? int.MaxValue : -3).ToArray();
            int expected = 17;

            foreach (int value in values) {
                expected = unchecked(expected + value);
            }

            Assert.Equal(expected, run(values));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Mul)]
    [InlineData(BinaryOp.And)]
    [InlineData(BinaryOp.Or)]
    [InlineData(BinaryOp.Xor)]
    public void WideIntegerReductionsCombineAllLanes(BinaryOp operation)
    {
        const long seed = 0x123456789ABCDEF;
        var body = CreateReduction(PrimType.Int64, ConstInt.CreateL(seed), operation);
        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<long[], long>>(body);

        foreach (int count in new[] { 0, 1, 3, 4, 5, 8, 17 }) {
            var values = Enumerable.Range(0, count).Select(i => (1L << (i + 32)) + i * 3 + 1).ToArray();
            long expected = seed;

            foreach (long value in values) {
                expected = operation switch {
                    BinaryOp.Mul => unchecked(expected * value),
                    BinaryOp.And => expected & value,
                    BinaryOp.Or => expected | value,
                    BinaryOp.Xor => expected ^ value,
                    _ => throw new InvalidOperationException()
                };
            }

            Assert.Equal(expected, run(values));
        }
    }

    [Fact]
    public void NarrowComparisonsCountEachMatchingLaneOnce()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Char.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value result = null!;

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var item = builder.CreateArrayLoad(body.Args[0], index);
            var condition = builder.CreateEq(item, ConstInt.CreateI('A'));
            result = loop.CreateAccum(ConstInt.CreateI(3), current => builder.CreateAdd(current, condition));
        });
        loop.Exit.CreateReturn(result);

        Vectorize(body, expected: true);
        var run = Utils.Compile<Func<char[], int>>(body);

        foreach (int count in new[] { 0, 15, 16, 17, 32, 35 }) {
            var values = Enumerable.Range(0, count).Select(i => i % 3 == 0 ? 'A' : '\uffff').ToArray();

            Assert.Equal(3 + values.Count(value => value == 'A'), run(values));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Shl)]
    [InlineData(BinaryOp.Shra)]
    [InlineData(BinaryOp.Shrl)]
    public void ShiftCountsUseScalarMasking(BinaryOp operation)
    {
        var body = CreatePointwiseLoop(PrimType.Int32, (builder, value) =>
            builder.Emit(new BinaryInst(operation, value, ConstInt.CreateI(33))));
        Vectorize(body, expected: true);
        int[] values = [int.MinValue, int.MaxValue, -17, 0, 1, -1, 2, 4, 7];
        var expected = values.Select(value => operation switch {
            BinaryOp.Shl => value << 33,
            BinaryOp.Shra => value >> 33,
            BinaryOp.Shrl => value >>> 33,
            _ => throw new InvalidOperationException()
        }).ToArray();

        Utils.Compile<Action<int[]>>(body)(values);

        Assert.Equal(expected, values);
    }

    [Fact]
    public void IntegerAbsPreservesOverflowAndCompletedStores()
    {
        var abs = _modResolver.Import(typeof(Math)).FindMethod("Abs", new MethodSig(PrimType.Int32, [PrimType.Int32]));
        var body = CreatePointwiseLoop(PrimType.Int32, (builder, value) => builder.CreateCall(abs, value));
        Vectorize(body, expected: false);
        var run = Utils.Compile<Action<int[]>>(body);
        int[] values = [-1, int.MinValue, -3, -4, -5, -6, -7, -8];

        Assert.Throws<OverflowException>(() => run(values));
        Assert.Equal(new[] { 1, int.MinValue, -3, -4, -5, -6, -7, -8 }, values);
    }

    [Theory]
    [InlineData("Min")]
    [InlineData("Max")]
    public void FloatingPointExtremaPreserveNaNsAndSignedZero(string name)
    {
        var method = _modResolver.Import(typeof(MathF)).FindMethod(name, new MethodSig(PrimType.Single, [PrimType.Single, PrimType.Single]));
        var body = CreatePointwiseLoop(PrimType.Single, (builder, value) => builder.CreateCall(method, value, ConstFloat.CreateS(0)));
        Vectorize(body, expected: false);
        float[] values = [0, -0.0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1, 1, -3, -0.0f];
        var expected = values.Select(value => BitConverter.SingleToInt32Bits(name == "Min" ? MathF.Min(value, 0) : MathF.Max(value, 0))).ToArray();

        Utils.Compile<Action<float[]>>(body)(values);

        Assert.Equal(expected, values.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void OffsetDependenciesRetainSequentialStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateSub(header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32), ConstInt.CreateI(1))), builder => {
            var current = builder.CreateArrayLoad(body.Args[0], index);
            builder.CreateArrayStore(body.Args[0], builder.CreateAdd(index, ConstInt.CreateI(1)), current);
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: false);
        var values = Enumerable.Range(7, 35).ToArray();
        Utils.Compile<Action<int[]>>(body)(values);

        Assert.All(values, value => Assert.Equal(7, value));
    }

    [Fact]
    public void PotentiallyOverlappingSpansRetainSequentialStores()
    {
        var spanType = _modResolver.Import(typeof(Span<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, spanType, spanType);
        var entry = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var source = body.CreateVar(spanType);
        var destination = body.CreateVar(spanType);
        builder.CreateStore(source, body.Args[0]);
        builder.CreateStore(destination, body.Args[1]);
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateCallVirt("get_Length", source)), items => {
            var value = items.CreateLoad(items.CreateCallVirt("get_Item", source, index));
            items.CreateStore(items.CreateCallVirt("get_Item", destination, index), value);
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: false);
        var values = Enumerable.Range(7, 35).ToArray();
        Utils.Compile<SpanCopy>(body)(values.AsSpan(0, 34), values.AsSpan(1, 34));

        Assert.All(values, value => Assert.Equal(7, value));
    }

    delegate void SpanCopy(Span<int> source, Span<int> destination);

    [Fact]
    public void FloatingPointReductionsRetainScalarOrder()
    {
        var body = CreateReduction(PrimType.Single, ConstFloat.CreateS(0), BinaryOp.FAdd);
        Vectorize(body, expected: false);
        var run = Utils.Compile<Func<float[], float>>(body);
        float[] values = [1e20f, 1, -1e20f, 3, 1e20f, 1, -1e20f, 3, -0.0f];
        float expected = 0;

        foreach (float value in values) {
            expected += value;
        }

        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(run(values)));
    }

    [Fact]
    public void UnprovenStorageExtentRetainsScalarAccesses()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateByref(), PrimType.Int32);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, body.Args[1]), builder => {
            builder.CreateStore(builder.CreatePtrOffset(body.Args[0], index), index);
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: false);
    }

    [Fact]
    public void CheckedOperationsRetainExceptionsAndCompletedStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var current = builder.CreateArrayLoad(body.Args[0], index);
            var next = builder.Emit(new BinaryInst(BinaryOp.AddOvf, current, ConstInt.CreateI(1)));
            builder.CreateArrayStore(body.Args[0], index, next);
        });
        loop.Exit.CreateReturn();

        Vectorize(body, expected: false);
        var run = Utils.Compile<Action<int[]>>(body);
        int[] values = [1, int.MaxValue, 3, 4, 5, 6, 7, 8];

        Assert.Throws<OverflowException>(() => run(values));
        Assert.Equal(new[] { 2, int.MaxValue, 3, 4, 5, 6, 7, 8 }, values);
    }

    private MethodBody CreatePointwiseLoop(TypeDesc type, Func<IRBuilder, Value, Value> transform)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, type.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var value = builder.CreateArrayLoad(body.Args[0], index);
            builder.CreateArrayStore(body.Args[0], index, transform(builder, value));
        });
        loop.Exit.CreateReturn();

        return body;
    }

    private MethodBody CreateReduction(TypeDesc type, Value seed, BinaryOp operation)
    {
        var body = Utils.CreateTestMethod(_modResolver, type, type.CreateArray());
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value result = null!;

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var item = builder.CreateArrayLoad(body.Args[0], index);
            result = loop.CreateAccum(seed, current => builder.Emit(new BinaryInst(operation, current, item)));
        });
        loop.Exit.CreateReturn(result);

        return body;
    }

    private void Vectorize(MethodBody body, bool expected)
    {
        Run(body, new SimplifyCFG(), new SsaPromotion(), new DeadCodeElim(), new SimplifyCFG(),
            new ValueNumbering(), new LoopStrengthReduction(), new DeadCodeElim(), new SimplifyCFG());
        var context = new MethodTransformContext(new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true }), body);
        var result = PassRunner.Run(new LoopVectorizer(_modResolver), context);

        if (expected != (result.Changes != MethodInvalidations.None)) {
            var dump = new StringWriter();
            IRPrinter.ExportPlain(body, dump);

            Assert.Fail(dump.ToString());
        }

        Assert.Equal(expected, body.Instructions().OfType<CallInst>().Any(call => call.Method.DeclaringType.Name.StartsWith("Vector256")));

        Run(body, new SimplifyInsts(_modResolver), new SimplifyCFG(), new DeadCodeElim());
    }
}
