namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class SimplifyInstsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BitTestSelectsPreserveNarrowValuesAcrossControlFlow(bool byteResult)
    {
        var type = byteResult ? PrimType.Byte : PrimType.Bool;
        int mask = byteResult ? 128 : 1;
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var compute = body.CreateBlock();
        var merge = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[1], merge, compute));
        var builder = new IRBuilder(compute);
        var bits = builder.CreateAnd(body.Args[0], ConstInt.CreateI(mask));
        var selected = builder.Emit(new SelectInst(bits, ConstInt.CreateI(0), ConstInt.CreateI(mask), type));
        compute.SetBranch(merge);
        var result = merge.InsertPhi(new PhiInst(type, (entry, ConstInt.Create(type, 0)), (compute, selected)));
        var output = new IRBuilder(result, InsertionDir.After);
        output.CreateReturn(output.CreateConvert(result, PrimType.Int32));

        Run(body, new SimplifyInsts(_modResolver));
        var run = Utils.Compile<Func<int, bool, int>>(body);
        foreach (int value in new[] { 0, 1, 127, 128, 255, -1, int.MinValue }) {
            Assert.Equal((value & mask) == 0 ? mask : 0, run(value, false));
            Assert.Equal(0, run(value, true));
        }
    }

    [Fact]
    public void Test_SimplifyInsts() => CheckIR("SimplifyInsts.ethil", new SimplifyInsts(_modResolver));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnaryOperationsPromoteSmallIntegersBeforeTruncation(bool wide)
    {
        var type = wide ? PrimType.UInt16 : PrimType.Byte;
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, type, type);
        var builder = new IRBuilder(body.CreateBlock());
        var complement = builder.Emit(new UnaryInst(UnaryOp.Not, body.Args[0]));
        builder.CreateReturn(builder.CreateEq(builder.CreateConvert(complement, type), body.Args[1]));
        Run(body, new SimplifyInsts(_modResolver));
        if (wide) Assert.True(Utils.Compile<Func<ushort, ushort, bool>>(body)(8, 65527));
        else Assert.True(Utils.Compile<Func<byte, byte, bool>>(body)(8, 247));
        var constant = Assert.IsType<ConstInt>(ConstFolding.FoldUnary(UnaryOp.Not, ConstInt.Create(type, 8)));
        Assert.Equal(PrimType.Int32, constant.ResultType);
        Assert.Equal(-9, constant.Value);
    }

    [Fact]
    public void UnboxingPreservesBoxMutationsAcrossLoopIterations()
    {
        var type = _modResolver.Import(typeof(ValueTuple<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type);
        var builder = new IRBuilder(body.CreateBlock());
        var box = builder.Emit(new CilIntrinsic.Box(type, body.Args[0]));
        var loop = new LoopBuilder(builder.Block);
        builder.Block.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value sum = null!;
        loop.Build(header => header.CreateSlt(index, ConstInt.CreateI(3)), items => {
            var address = items.Emit(new CilIntrinsic.UnboxRef(type, box));
            var field = type.FindField("Item1");
            var next = items.CreateAdd(items.CreateFieldLoad(field, address), ConstInt.CreateI(1));
            items.CreateFieldStore(field, address, next);
            sum = loop.CreateAccum(ConstInt.CreateI(0), current => items.CreateAdd(current, next));
        });
        loop.Exit.CreateReturn(sum);
        Run(body, new SimplifyInsts(_modResolver));
        Assert.Equal(36, Utils.Compile<Func<ValueTuple<int>, int>>(body)(new(10)));
    }

    [Fact]
    public void StructDelegateTargetsUseAnUnboxedReceiver()
    {
        var type = _modResolver.Import(typeof(DateTime));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type);
        var builder = new IRBuilder(body.CreateBlock());
        var box = builder.Emit(new CilIntrinsic.Box(type, body.Args[0]));
        var target = builder.Emit(new FuncAddrInst(type.FindMethod("get_Year")));
        var func = _modResolver.Import(typeof(Func<int>));
        var allocation = builder.CreateNewObj(func.FindMethod(".ctor"), box, target);
        builder.CreateReturn(builder.CreateCallVirt("Invoke", allocation));
        Run(body, new SimplifyInsts(_modResolver));
        Assert.Single(body.Instructions().OfType<CilIntrinsic.UnboxRef>());
        Assert.Equal(2026, Utils.Compile<Func<DateTime, int>>(body)(new DateTime(2026, 11, 5)));
    }

    [Fact]
    public void ReturnedBoxAddressesRetainTheirHeapStorage()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32.CreateByref(), PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var box = builder.Emit(new CilIntrinsic.Box(PrimType.Int32, body.Args[0]));
        builder.CreateReturn(builder.Emit(new CilIntrinsic.UnboxRef(PrimType.Int32, box)));
        Run(body, new SimplifyInsts(_modResolver));
        Assert.Single(body.Instructions().OfType<CilIntrinsic.Box>());
        var run = Utils.Compile<RefGetter>(body);
        ref int value = ref run(42);
        GC.Collect();
        Assert.Equal(42, value);
    }

    public delegate ref int RefGetter(int value);

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(true, -1)]
    public void PointerAddressSimplificationPreservesProductWidthAndSign(bool nativeProduct, int scale)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.IntPtr, PrimType.IntPtr, PrimType.Int32);
        var ib = new IRBuilder(body.CreateBlock());
        Value index = nativeProduct ? ib.CreateConvert(body.Args[1], PrimType.IntPtr) : body.Args[1];
        var product = ib.CreateMul(index, ConstInt.CreateI(scale));
        var displacement = nativeProduct ? product : ib.CreateConvert(product, PrimType.IntPtr);
        ib.CreateReturn(ib.CreateAdd(body.Args[0], displacement));

        Run(body, new SimplifyInsts(_modResolver));
        var run = Utils.Compile<Func<nint, int, nint>>(body);
        foreach (int value in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
            nint expected = nativeProduct ? unchecked(100 + (nint)value * scale) : unchecked(100 + (nint)(value * scale));
            Assert.Equal(expected, run(100, value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedLambdaReceiversMoveOnlyAfterTheirTypeHasInitialized(bool separateReceiverType)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var module = body.Definition.Module;
        var cacheType = module.CreateType(null, "LambdaCache");
        cacheType.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(
            _modResolver.Import(typeof(CompilerGeneratedAttribute)).FindMethod(".ctor")));
        var receiverType = separateReceiverType ? module.CreateType(null, "Receiver") : cacheType;
        var funcType = _modResolver.Import(typeof(Func<int>));
        var cache = cacheType.CreateField("Cache", funcType, FieldAttributes.Public | FieldAttributes.Static);
        var receiver = receiverType.CreateField("Instance", PrimType.String,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var initializer = receiverType.CreateMethod(".cctor", PrimType.Void, [],
            MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        initializer.Body = new MethodBody(initializer);
        var init = new IRBuilder(initializer.Body.CreateBlock());
        init.CreateFieldStore(receiver, null, ConstString.Create("abc"));
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();

        var entry = body.CreateBlock();
        var allocate = body.CreateBlock();
        var merge = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var cached = builder.CreateFieldLoad(cache);
        entry.SetBranch(new BranchInst(builder.CreateNe(cached, ConstNull.Create()), merge, allocate));
        builder.SetPosition(allocate);
        var instance = builder.CreateFieldLoad(receiver);
        var target = builder.Emit(new FuncAddrInst(_modResolver.Import(typeof(string)).FindMethod("get_Length")));
        var allocation = builder.CreateNewObj(funcType.FindMethod(".ctor"), instance, target);
        builder.CreateFieldStore(cache, null, allocation);
        allocate.SetBranch(merge);
        builder.SetPosition(merge);
        var lambda = builder.CreatePhi(funcType, (entry, cached), (allocate, allocation));
        var loop = new LoopBuilder(merge);
        merge.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        Value sum = null!;
        CallInst invoke = null!;
        loop.Build(header => header.CreateSlt(index, body.Args[0]), items => {
            invoke = items.CreateCallVirt("Invoke", lambda);
            sum = loop.CreateAccum(ConstInt.CreateI(0), current => items.CreateAdd(current, invoke));
        });
        loop.Exit.CreateReturn(loop.Exit.CreateAdd(sum, loop.Exit.CreateFieldLoad(effect)));
        Run(body, new SimplifyInsts(_modResolver));

        Assert.Equal("get_Length", invoke.Method.Name);
        var rematerialized = Assert.IsType<LoadInst>(invoke.Args[0]);
        Assert.Same(separateReceiverType ? invoke.Block : entry, rematerialized.Block);
        Assert.True(rematerialized.MayThrow);
        Run(body, new SimplifyCFG(), new DeadCodeElim());
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(50, run(0));
        Assert.Equal(59, run(3));
    }

    [Theory]
    [InlineData(UnaryOp.Neg)]
    [InlineData(UnaryOp.Not)]
    [InlineData(UnaryOp.FNeg)]
    public void SimplifyUnaryCancelsBothOperations(UnaryOp op)
    {
        var type = op == UnaryOp.FNeg ? PrimType.Double : PrimType.Int32;
        var body = Utils.CreateTestMethod(_modResolver, type, type);
        var ib = new IRBuilder(body.CreateBlock());
        var inner = ib.Emit(new UnaryInst(op, body.Args[0]));
        var outer = ib.Emit(new UnaryInst(op, inner));
        var ret = ib.CreateReturn(outer);
        Run(body, new SimplifyInsts(_modResolver));

        Assert.Same(body.Args[0], ret.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksCoalesceWithFieldDereferences(bool duplicate)
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var field = type.FindField("Value");
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        var increment = builder.CreateAdd(body.Args[1], ConstInt.CreateI(1));

        if (duplicate) {
            builder.CreateNullCheck(body.Args[0]);
        }

        builder.CreateReturn(builder.CreateAdd(builder.CreateFieldLoad(field, body.Args[0]), increment));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, int, int>>(body);

        Assert.Equal(10, run(new(7), 2));
        Assert.Throws<NullReferenceException>(() => run(null!, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksStayBeforeObservableOperations(bool throwingOperation)
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);

        if (throwingOperation) {
            builder.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(10), body.Args[1]));
        } else {
            builder.CreateFieldStore(effect, null, ConstInt.CreateI(1));
        }

        builder.CreateReturn(builder.CreateFieldLoad(type.FindField("Value"), body.Args[0]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, int, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, 0));
        Assert.Equal(0, run.Method.DeclaringType!.GetField("Effect")!.GetValue(null));

        if (throwingOperation) {
            Assert.Throws<DivideByZeroException>(() => run(new(7), 0));
        } else {
            Assert.Equal(7, run(new(7), 0));
            Assert.Equal(1, run.Method.DeclaringType!.GetField("Effect")!.GetValue(null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksCoalesceWithArrayAccesses(bool length)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(length ? builder.CreateConvert(builder.CreateArrayLen(body.Args[0]), PrimType.Int32)
            : builder.CreateArrayLoad(body.Args[0], body.Args[1]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<int[], int, int>>(body);

        Assert.Equal(length ? 1 : 7, run([7], 0));
        Assert.Throws<NullReferenceException>(() => run(null!, -1));

        if (!length) {
            Assert.Throws<IndexOutOfRangeException>(() => run([7], -1));
        }
    }

    [Fact]
    public void NullChecksCoalesceWithVirtualCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.String);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(builder.CreateCallVirt("get_Length", body.Args[0]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<string, int>>(body);

        Assert.Equal(3, run("abc"));
        Assert.Throws<NullReferenceException>(() => run(null!));
    }

    [Fact]
    public void NullChecksRetainTheCheckedReceiver()
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, type);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(builder.CreateFieldLoad(type.FindField("Value"), body.Args[1]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, StrongBox<int>, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, new(7)));
        Assert.Equal(7, run(new(1), new(7)));
    }

    [Fact]
    public void NullChecksDoNotMoveAcrossConditionalDereferences()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Bool);
        var entry = body.CreateBlock();
        var read = body.CreateBlock();
        var skip = body.CreateBlock();
        var builder = new IRBuilder(entry);
        builder.CreateNullCheck(body.Args[0]);
        builder.SetBranch(body.Args[1], read, skip);
        var reader = new IRBuilder(read);
        reader.CreateReturn(reader.CreateConvert(reader.CreateArrayLen(body.Args[0]), PrimType.Int32));
        skip.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<int[], bool, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, false));
        Assert.Throws<NullReferenceException>(() => run(null!, true));
        Assert.Equal(0, run([1, 2], false));
        Assert.Equal(2, run([1, 2], true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DictionaryLookupCoalescingRequiresProvenComparerAndEdge(bool bypassEdge, bool subclass)
    {
        var dictionary = _modResolver.Import(typeof(Dictionary<string, int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool, PrimType.String);
        var entry = body.CreateBlock();
        var check = body.CreateBlock();
        var read = body.CreateBlock();
        var missing = body.CreateBlock();
        var ib = new IRBuilder(entry);
        MethodDesc ctor = dictionary.FindMethod(".ctor", new MethodSig(PrimType.Void, []));

        if (subclass) {
            var derived = body.Definition.Module.CreateType(null, "DerivedDictionary", baseType: dictionary);
            var constructor = derived.CreateMethod(".ctor", PrimType.Void, [new ParamDef(derived, "this")],
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            constructor.Body = new MethodBody(constructor);
            var init = new IRBuilder(constructor.Body.CreateBlock());
            init.CreateCall(ctor, constructor.Body.Args[0]);
            init.CreateReturn();
            ctor = constructor;
        }

        var dict = ib.CreateNewObj(ctor);
        ib.CreateCallVirt("Add", dict, body.Args[1], ConstInt.CreateI(7));

        if (bypassEdge) {
            ib.SetBranch(body.Args[0], read, check);
        } else {
            ib.SetBranch(check);
        }

        var cb = new IRBuilder(check);
        cb.SetBranch(cb.CreateCallVirt("ContainsKey", dict, body.Args[1]), read, missing);
        var rb = new IRBuilder(read);
        rb.CreateReturn(rb.CreateCallVirt("get_Item", dict, body.Args[1]));
        missing.SetBranch(new ReturnInst(ConstInt.CreateI(-1)));
        Run(body, new SimplifyInsts(_modResolver));

        Assert.Equal(!bypassEdge && !subclass, body.Instructions().OfType<CallInst>().Any(c => c.Method.Name == "TryGetValue"));

        var run = Utils.Compile<Func<bool, string, int>>(body);

        Assert.Equal(7, run(true, "key"));
        Assert.Equal(7, run(false, "key"));
    }

    [Theory]
    [InlineData(BinaryOp.Mul, BinaryOp.Add, false)]
    [InlineData(BinaryOp.Mul, BinaryOp.Sub, false)]
    [InlineData(BinaryOp.And, BinaryOp.Or, false)]
    [InlineData(BinaryOp.And, BinaryOp.Xor, false)]
    [InlineData(BinaryOp.Or, BinaryOp.And, false)]
    [InlineData(BinaryOp.Mul, BinaryOp.Add, true)]
    [InlineData(BinaryOp.Mul, BinaryOp.Sub, true)]
    [InlineData(BinaryOp.And, BinaryOp.Or, true)]
    [InlineData(BinaryOp.And, BinaryOp.Xor, true)]
    [InlineData(BinaryOp.Or, BinaryOp.And, true)]
    public void CommonOperandsFactorAcrossDistributiveOperations(BinaryOp inner, BinaryOp outer, bool wide)
    {
        var type = wide ? PrimType.Int64 : PrimType.Int32;
        for (int order = 0; order < 4; order++) {
            var body = Utils.CreateTestMethod(_modResolver, type, type, type, type);
            var builder = new IRBuilder(body.CreateBlock());
            var left = builder.CreateBin(inner, body.Args[order & 1], body.Args[1 - (order & 1)]);
            var right = builder.CreateBin(inner, body.Args[(order & 2) != 0 ? 2 : 0], body.Args[(order & 2) != 0 ? 0 : 2]);
            builder.CreateReturn(builder.CreateBin(outer, left, right));
            body = Simplify(body);
            Assert.Equal(2, CountOperations(body));
            var run = CompileTernary(body, wide);
            foreach (long x in Values(wide)) foreach (long a in Values(wide)) foreach (long b in Values(wide)) {
                Assert.Equal(Normalize(Evaluate(outer, Evaluate(inner, x, a), Evaluate(inner, x, b)), wide), run(x, a, b));
            }
        }
    }

    [Theory]
    [InlineData(BinaryOp.Mul, BinaryOp.Add, false)]
    [InlineData(BinaryOp.And, BinaryOp.Or, false)]
    [InlineData(BinaryOp.And, BinaryOp.Xor, false)]
    [InlineData(BinaryOp.Or, BinaryOp.And, false)]
    [InlineData(BinaryOp.Mul, BinaryOp.Add, true)]
    [InlineData(BinaryOp.And, BinaryOp.Or, true)]
    [InlineData(BinaryOp.And, BinaryOp.Xor, true)]
    [InlineData(BinaryOp.Or, BinaryOp.And, true)]
    public void AssociativeChainsCombineConstantContributions(BinaryOp inner, BinaryOp outer, bool wide)
    {
        var type = wide ? PrimType.Int64 : PrimType.Int32;
        long[] constants = inner == BinaryOp.Mul ? [2, 3, 5] : [3, 6, 0x100, wide ? long.MinValue : int.MinValue];
        if (inner == BinaryOp.Or) constants = constants.Select(value => ~value).ToArray();
        var body = Utils.CreateTestMethod(_modResolver, type, type, type);
        var builder = new IRBuilder(body.CreateBlock());
        Value result = body.Args[0];
        foreach (long constant in constants) {
            result = builder.CreateBin(outer, builder.CreateBin(inner, body.Args[1], ConstInt.Create(type, constant)), result);
        }
        builder.CreateReturn(result);
        body = Simplify(body);
        Assert.Equal(2, CountOperations(body));
        var run = CompileBinary(body, wide);
        foreach (long a in Values(wide)) foreach (long x in Values(wide)) {
            long expected = a;
            foreach (long constant in constants) expected = Evaluate(outer, expected, Evaluate(inner, x, constant));
            Assert.Equal(Normalize(expected, wide), run(a, x));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Add)]
    [InlineData(BinaryOp.Mul)]
    [InlineData(BinaryOp.And)]
    [InlineData(BinaryOp.Or)]
    [InlineData(BinaryOp.Xor)]
    public void SelectsFactorCommonOperands(BinaryOp op)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateSelect(body.Args[0],
            builder.CreateBin(op, body.Args[1], body.Args[2]), builder.CreateBin(op, body.Args[3], body.Args[1])));
        body = Simplify(body);
        Assert.Equal(1, CountOperations(body));
        var run = Utils.Compile<Func<bool, int, int, int, int>>(body);
        foreach (int x in Values(false).Select(x => (int)x)) {
            Assert.Equal((int)Evaluate(op, x, 0xFF00), run(true, x, 0xFF00, 0x00FF));
            Assert.Equal((int)Evaluate(op, x, 0x00FF), run(false, x, 0xFF00, 0x00FF));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Or)]
    [InlineData(BinaryOp.And)]
    [InlineData(BinaryOp.Xor)]
    public void ConditionalFlagUpdatesCombineInTheDefaultPipeline(BinaryOp op)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var block = body.CreateBlock();
        Value value = body.Args[0];
        int[] masks = [16, 256, 32768, 16384];

        foreach (int mask in masks) {
            var update = body.CreateBlock();
            var merge = body.CreateBlock();
            var builder = new IRBuilder(block, InsertionDir.After);
            builder.SetBranch(builder.CreateNe(builder.CreateAnd(body.Args[1], ConstInt.CreateI(mask)), ConstInt.CreateI(0)), update, merge);
            var updater = new IRBuilder(update);
            var changed = updater.CreateBin(op, value, ConstInt.CreateI(op == BinaryOp.And ? ~mask : mask));
            updater.SetBranch(merge);
            value = merge.InsertPhi(new PhiInst(PrimType.Int32, (block, value), (update, changed)));
            block = merge;
        }
        block.SetBranch(new ReturnInst(value));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).ThrowIfFailed();
        body = body.Definition.Body!;

        var il = ILGenerator.GenerateCode(body);
        Assert.Equal(op == BinaryOp.And ? 3 : 2, CountOperations(body));
        Assert.DoesNotContain(il.Instructions, inst => inst.FlowControl is ILFlowControl.Branch or ILFlowControl.CondBranch);
        var run = Utils.Compile<Func<int, int, int>>(body);
        foreach (int a in new[] { 0, -1, int.MinValue, 0x1111, 0xC110 }) {
            for (int subset = 0; subset < 16; subset++) {
                int flags = int.MinValue | 2;
                for (int bit = 0; bit < masks.Length; bit++) {
                    if ((subset & (1 << bit)) != 0) flags |= masks[bit];
                }
                int expected = a;
                foreach (int mask in masks) {
                    if ((flags & mask) != 0) expected = (int)Evaluate(op, expected, op == BinaryOp.And ? ~mask : mask);
                }
                Assert.Equal(expected, run(a, flags));
            }
        }
    }

    [Theory]
    [InlineData(BinaryOp.Or, false)]
    [InlineData(BinaryOp.And, false)]
    [InlineData(BinaryOp.Xor, false)]
    [InlineData(BinaryOp.Or, true)]
    [InlineData(BinaryOp.And, true)]
    [InlineData(BinaryOp.Xor, true)]
    public void ConditionalBitUpdatesHandleTestPolarityAndSignBits(BinaryOp op, bool wide)
    {
        var type = wide ? PrimType.Int64 : PrimType.Int32;
        long mask = wide ? long.MinValue : int.MinValue;
        for (int variant = 0; variant < 8; variant++) {
            bool equal = (variant & 1) != 0;
            bool compareMask = (variant & 2) != 0;
            bool updateWhenSet = (variant & 4) != 0;
            bool trueWhenSet = equal == compareMask;
            var body = Utils.CreateTestMethod(_modResolver, type, type, type);
            var builder = new IRBuilder(body.CreateBlock());
            var bits = builder.CreateAnd(ConstInt.Create(type, mask), body.Args[1]);
            var condition = builder.CreateCmp(equal ? CompareOp.Eq : CompareOp.Ne,
                ConstInt.Create(type, compareMask ? mask : 0), bits);
            var updated = builder.CreateBin(op, ConstInt.Create(type, op == BinaryOp.And ? ~mask : mask), body.Args[0]);
            builder.CreateReturn(builder.CreateSelect(condition,
                trueWhenSet == updateWhenSet ? updated : body.Args[0],
                trueWhenSet == updateWhenSet ? body.Args[0] : updated));
            body = Simplify(body);

            Assert.DoesNotContain(ILGenerator.GenerateCode(body).Instructions, inst => inst.FlowControl is ILFlowControl.Branch or ILFlowControl.CondBranch);
            var run = CompileBinary(body, wide);
            foreach (long a in new[] { 0, -1, mask, ~mask, 0x1234 }) {
                foreach (long flags in new[] { 0, -1, mask, ~mask, 0x1234 }) {
                    long expected = ((flags & mask) != 0) == updateWhenSet
                        ? Evaluate(op, a, op == BinaryOp.And ? ~mask : mask) : a;
                    Assert.Equal(Normalize(expected, wide), run(a, flags));
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(3, 3)]
    [InlineData(16, 256)]
    [InlineData(-1, -1)]
    public void ConditionalUpdatesPreserveMultiBitAndMismatchedMasks(int testMask, int updateMask)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var condition = builder.CreateNe(builder.CreateAnd(body.Args[1], ConstInt.CreateI(testMask)), ConstInt.CreateI(0));
        builder.CreateReturn(builder.CreateSelect(condition, builder.CreateOr(body.Args[0], ConstInt.CreateI(updateMask)), body.Args[0]));
        body = Simplify(body);
        var run = Utils.Compile<Func<int, int, int>>(body);
        foreach (int a in new[] { 0, -1, 1, 2, 16, 256, int.MinValue }) {
            foreach (int flags in new[] { 0, -1, 1, 2, 16, 256, int.MinValue }) {
                Assert.Equal((flags & testMask) != 0 ? a | updateMask : a, run(a, flags));
            }
        }
    }

    [Fact]
    public void SelectFactoringRetainsObservableOperands()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32,
            PrimType.Bool, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Int32.CreateArray());
        var builder = new IRBuilder(body.CreateBlock());
        var left = builder.CreateAnd(body.Args[1], builder.CreateArrayLoad(body.Args[2], ConstInt.CreateI(0)));
        var right = builder.CreateAnd(body.Args[1], builder.CreateArrayLoad(body.Args[3], ConstInt.CreateI(0)));
        builder.CreateReturn(builder.CreateSelect(body.Args[0], left, right));
        body = Simplify(body);
        var run = Utils.Compile<Func<bool, int, int[], int[], int>>(body);
        Assert.Equal(1, run(true, 3, [1], [2]));
        Assert.Equal(2, run(false, 3, [1], [2]));
        Assert.Throws<NullReferenceException>(() => run(true, 0, [1], null!));
        Assert.Throws<NullReferenceException>(() => run(false, 0, null!, [2]));
    }

    [Fact]
    public void BitTestSelectsPreserveCommonConstantBits()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.UInt32, PrimType.UInt32);
        var builder = new IRBuilder(body.CreateBlock());
        var condition = builder.CreateAnd(body.Args[0], ConstInt.Create(PrimType.UInt32, 16));
        builder.CreateReturn(builder.CreateSelect(condition,
            ConstInt.Create(PrimType.UInt32, 0x80000010), ConstInt.Create(PrimType.UInt32, 0x80000000)));
        body = Simplify(body);
        Assert.DoesNotContain(ILGenerator.GenerateCode(body).Instructions, inst => inst.FlowControl == ILFlowControl.CondBranch);
        var run = Utils.Compile<Func<uint, uint>>(body);
        foreach (uint flags in new[] { 0U, 16U, 17U, 0x80000000U, uint.MaxValue }) {
            Assert.Equal(0x80000000U | (flags & 16U), run(flags));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Or)]
    [InlineData(BinaryOp.And)]
    public void UnsignedAllOnesIdentitiesUseTheStackWidth(BinaryOp op)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.UInt32, PrimType.UInt32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.Emit(new BinaryInst(op, ConstInt.Create(PrimType.UInt32, uint.MaxValue), body.Args[0])));
        body = Simplify(body);
        Assert.Equal(0, CountOperations(body));
        var run = Utils.Compile<Func<uint, uint>>(body);
        foreach (uint value in new[] { 0U, 16U, 0x80000000U, uint.MaxValue }) {
            Assert.Equal(op == BinaryOp.And ? value : uint.MaxValue, run(value));
        }
    }

    [Theory]
    [InlineData(BinaryOp.And)]
    [InlineData(BinaryOp.Mul)]
    public void ZeroConstantsPreserveNativeResultWidths(BinaryOp op)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.IntPtr, PrimType.IntPtr);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateBin(op, ConstInt.CreateI(0), body.Args[0]));
        body = Simplify(body);
        var run = Utils.Compile<Func<nint, nint>>(body);
        Assert.Equal((nint)0, run(nint.MinValue));
        Assert.Equal((nint)0, run(nint.MaxValue));
    }

    [Fact]
    public void FactoringPreservesSharedTermsAndDistinctSources()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var shared = builder.CreateAnd(body.Args[1], ConstInt.CreateI(16));
        var combined = builder.CreateOr(shared, builder.CreateAnd(body.Args[1], ConstInt.CreateI(256)));
        var distinct = builder.CreateOr(body.Args[0], builder.CreateOr(combined, builder.CreateAnd(body.Args[2], ConstInt.CreateI(32768))));
        builder.CreateReturn(builder.CreateAdd(shared, distinct));
        body = Simplify(body);
        var run = Utils.Compile<Func<int, int, int, int>>(body);
        foreach (int a in new[] { 0, 16, -1 }) {
            foreach (int flags in new[] { 0, 16, 256, -1 }) {
                foreach (int other in new[] { 0, 32768 }) {
                    Assert.Equal(unchecked((flags & 16) + (a | (flags & 272) | (other & 32768))), run(a, flags, other));
                }
            }
        }
    }

    [Theory]
    [InlineData(BinaryOp.Add, false)]
    [InlineData(BinaryOp.Sub, false)]
    [InlineData(BinaryOp.Add, true)]
    [InlineData(BinaryOp.Sub, true)]
    public void BareFactorsSupplyTheMultiplicativeIdentity(BinaryOp op, bool bareFirst)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var product = builder.CreateMul(body.Args[0], ConstInt.CreateI(3));
        builder.CreateReturn(builder.CreateBin(op, bareFirst ? body.Args[0] : product, bareFirst ? product : body.Args[0]));
        body = Simplify(body);
        Assert.Equal(1, CountOperations(body));
        var run = Utils.Compile<Func<int, int>>(body);
        foreach (int x in Values(false).Select(x => (int)x)) {
            Assert.Equal((int)Evaluate(op, bareFirst ? x : unchecked(x * 3), bareFirst ? unchecked(x * 3) : x), run(x));
        }
    }

    [Theory]
    [InlineData(BinaryOp.Or, BinaryOp.And, 1, 2)]
    [InlineData(BinaryOp.Xor, BinaryOp.And, 1, 2)]
    [InlineData(BinaryOp.And, BinaryOp.Or, 1, -2)]
    [InlineData(BinaryOp.Add, BinaryOp.Mul, 0, 2)]
    [InlineData(BinaryOp.Sub, BinaryOp.Mul, 0, 2)]
    public void DistributionEliminatesConstantContributions(BinaryOp nested, BinaryOp outer, int constant, int scale)
    {
        foreach (bool wide in new[] { false, true }) {
            var type = wide ? PrimType.Int64 : PrimType.Int32;
            long offset = outer == BinaryOp.Mul ? wide ? long.MinValue : int.MinValue : constant;
            var body = Utils.CreateTestMethod(_modResolver, type, type, type, type);
            var builder = new IRBuilder(body.CreateBlock());
            var expression = builder.CreateBin(nested, body.Args[0], ConstInt.Create(type, offset));
            builder.CreateReturn(builder.CreateBin(outer, ConstInt.Create(type, scale), expression));
            body = Simplify(body);
            Assert.Equal(1, CountOperations(body));
            var run = CompileTernary(body, wide);
            foreach (long x in Values(wide)) {
                Assert.Equal(Normalize(Evaluate(outer, Evaluate(nested, x, offset), scale), wide), run(x, 0, 0));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubtractionRetainsItsOperandOrder(bool constantFirst)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var x = body.Args[1];
        builder.CreateReturn(constantFirst
            ? builder.CreateMul(builder.CreateSub(ConstInt.CreateI(int.MinValue), x), ConstInt.CreateI(2))
            : builder.CreateSub(builder.CreateSub(body.Args[0], builder.CreateMul(x, ConstInt.CreateI(2))),
                builder.CreateMul(x, ConstInt.CreateI(3))));
        body = Simplify(body);
        var run = Utils.Compile<Func<int, int, int>>(body);
        foreach (int a in new[] { 0, int.MinValue, int.MaxValue }) foreach (int value in new[] { -1, 0, 1, int.MinValue, int.MaxValue }) {
            Assert.Equal(constantFirst ? unchecked((int.MinValue - value) * 2) : unchecked(a - value * 2 - value * 3), run(a, value));
        }
    }

    [Fact]
    public void FactoringPreservesObservableUsesOfSharedProducts()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32.CreateArray());
        var builder = new IRBuilder(body.CreateBlock());
        var product = builder.CreateMul(body.Args[0], ConstInt.CreateI(2));
        builder.CreateArrayStore(body.Args[1], ConstInt.CreateI(0), product);
        builder.CreateReturn(builder.CreateAdd(product, builder.CreateMul(body.Args[0], ConstInt.CreateI(3))));
        body = Simplify(body);
        var run = Utils.Compile<Func<int, int[], int>>(body);
        foreach (int x in Values(false).Select(x => (int)x)) {
            int[] output = [0];
            Assert.Equal(unchecked(x * 5), run(x, output));
            Assert.Equal(unchecked(x * 2), output[0]);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CheckedArithmeticPreservesOverflow(bool checkedProducts, bool checkedSum)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var left = builder.CreateBin(checkedProducts ? BinaryOp.MulOvf : BinaryOp.Mul, body.Args[0], ConstInt.CreateI(2));
        var right = builder.CreateBin(checkedProducts ? BinaryOp.MulOvf : BinaryOp.Mul,
            body.Args[0], ConstInt.CreateI(checkedProducts ? -2 : 1));
        builder.CreateReturn(builder.CreateBin(checkedSum ? BinaryOp.AddOvf : BinaryOp.Add, left, right));
        body = Simplify(body);
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(checkedProducts ? 0 : 3, run(1));
        Assert.Throws<OverflowException>(() => run(checkedProducts ? int.MaxValue : 1_000_000_000));
    }

    [Fact]
    public void FloatingPointProductsRetainTheirRoundingAndNaNs()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Double, PrimType.Double, PrimType.Double, PrimType.Double);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateFAdd(builder.CreateFMul(body.Args[0], body.Args[1]), builder.CreateFMul(body.Args[0], body.Args[2])));
        body = Simplify(body);
        var run = Utils.Compile<Func<double, double, double, double>>(body);
        Assert.True(double.IsNaN(run(double.PositiveInfinity, 0, 1)));
        Assert.True(double.IsNaN(run(double.MaxValue, 2, -2)));
        Assert.Equal(21, run(3, 2, 5));
    }

    [Fact]
    public void MixedNativeOperandsRetainIntermediateWidths()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.IntPtr, PrimType.IntPtr, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(builder.CreateMul(body.Args[0], body.Args[1]), builder.CreateMul(body.Args[0], body.Args[2])));
        body = Simplify(body);
        var run = Utils.Compile<Func<nint, int, int, nint>>(body);
        Assert.Equal(unchecked((nint)int.MaxValue + 1), run(1, int.MaxValue, 1));
        Assert.Equal(unchecked((nint)int.MinValue - 1), run(1, int.MinValue, -1));
    }

    [Fact]
    public void UnprofitableExpansionKeepsTheProductFactored()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateMul(body.Args[0], builder.CreateAdd(body.Args[1], body.Args[2])));
        body = Simplify(body);
        Assert.Equal(2, CountOperations(body));
        var run = Utils.Compile<Func<int, int, int, int>>(body);
        Assert.Equal(unchecked(int.MaxValue * 5), run(int.MaxValue, 2, 3));
    }

    private MethodBody Simplify(MethodBody body)
    {
        var manager = new PassManager() {
            Compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true })
        };
        manager.AddPasses().Apply<SimplifyInsts>().Apply<DeadCodeElim>().RepeatUntilFixedPoint(3);
        manager.Run([body.Definition]).ThrowIfFailed();
        return body.Definition.Body!;
    }

    private static int CountOperations(MethodBody body)
        => ILGenerator.GenerateCode(body).Instructions.Count(inst => inst.OpCode is ILCode.Add or ILCode.Sub or ILCode.Mul or ILCode.And or ILCode.Or or ILCode.Xor or ILCode.Not);

    private static long[] Values(bool wide) => [0, -1, 1, 7, 0x5555, 0xAAAA, wide ? long.MinValue : int.MinValue, wide ? long.MaxValue : int.MaxValue];

    private static Func<long, long, long> CompileBinary(MethodBody body, bool wide)
    {
        if (wide) return Utils.Compile<Func<long, long, long>>(body);
        var run = Utils.Compile<Func<int, int, int>>(body);
        return (a, flags) => run((int)a, (int)flags);
    }

    private static Func<long, long, long, long> CompileTernary(MethodBody body, bool wide)
    {
        if (wide) return Utils.Compile<Func<long, long, long, long>>(body);
        var run = Utils.Compile<Func<int, int, int, int>>(body);
        return (x, a, b) => run((int)x, (int)a, (int)b);
    }

    private static long Normalize(long value, bool wide) => wide ? value : (int)value;

    private static long Evaluate(BinaryOp op, long left, long right) => op switch {
        BinaryOp.Add => unchecked(left + right),
        BinaryOp.Sub => unchecked(left - right),
        BinaryOp.Mul => unchecked(left * right),
        BinaryOp.And => left & right,
        BinaryOp.Or => left | right,
        BinaryOp.Xor => left ^ right,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };
}
