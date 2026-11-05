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
}
