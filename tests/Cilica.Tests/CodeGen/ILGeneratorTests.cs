namespace Cilica.Tests.CodeGen;

using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Tests.Passes;
using System.Reflection;

[Collection("ModuleResolver")]
public class ILGeneratorTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public unsafe void PointerLoadsAndStoresSurviveRepeatedCompilation()
    {
        var pointer = PrimType.Byte.CreatePointer();
        var body = Utils.CreateTestMethod(_modResolver, pointer, pointer.CreatePointer(), pointer);
        var builder = new IRBuilder(body.CreateBlock());
        builder.Emit(new StoreInst(body.Args[0], body.Args[1], pointer));
        builder.CreateReturn(builder.Emit(new LoadInst(body.Args[0], pointer)));
        byte value = 42;
        byte* storage = null;
        for (int round = 0; round < 3; round++) {
            body.Definition.ILBody = ILGenerator.GenerateCode(body);
            body = Cilica.Frontend.ILImporter.ParseCode(body.Definition);
            body.Definition.Body = body;
            IRVerifier.Verify(body);
            var run = Utils.Compile<UpdatePointer>(body);
            Assert.Equal((nint)(&value), (nint)run(&storage, &value));
            Assert.Equal((nint)(&value), (nint)storage);
            Assert.Equal(42, *storage);
        }
    }

    unsafe delegate byte* UpdatePointer(byte** address, byte* value);

    [Fact]
    public void Test_ILGenerator() => CheckCIL("CodeGen/StructFieldInsertExtract.ethil");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StackAllocationsInCallArgumentsKeepAnEmptyStackAndEvaluationOrder(bool wrapInSpan)
    {
        var callback = _modResolver.Import(typeof(Func<int>));
        var span = _modResolver.Import(typeof(Span<byte>));
        var pointer = PrimType.Void.CreatePointer();
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, callback, callback);
        var consume = body.Definition.DeclaringType.CreateMethod("Consume", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "prefix"), new ParamDef(wrapInSpan ? span : pointer, "buffer")],
            MethodAttributes.Public | MethodAttributes.Static);
        consume.ImplAttribs = MethodImplAttributes.NoInlining;
        consume.Body = new Cilica.IR.MethodBody(consume);
        var callee = new IRBuilder(consume.Body.CreateBlock());
        Value count;
        if (wrapInSpan) {
            var local = consume.Body.CreateVar(span);
            callee.CreateStore(local, consume.Body.Args[1]);
            count = callee.CreateCall(span.FindMethod("get_Length"), local);
        } else {
            callee.Emit(new StoreInst(consume.Body.Args[1], ConstInt.CreateI(8), PrimType.Byte));
            count = callee.Emit(new LoadInst(consume.Body.Args[1], PrimType.Byte));
        }
        callee.CreateReturn(callee.CreateAdd(consume.Body.Args[0], count));
        var builder = new IRBuilder(body.CreateBlock());
        var prefix = builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]);
        var size = builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[1]);
        var allocation = builder.Emit(new CilIntrinsic.Alloca(builder.CreateConvert(size, PrimType.UIntPtr)));
        Value buffer = wrapInSpan
            ? builder.CreateNewObj(span.FindMethod(".ctor", new MethodSig(PrimType.Void, [pointer, PrimType.Int32])), allocation, ConstInt.CreateI(8))
            : allocation;
        builder.CreateReturn(builder.CreateCall(consume, prefix, buffer));
        IRVerifier.Verify(body);

        var trace = new List<string>();
        var run = Utils.Compile<Func<Func<int>, Func<int>, int>>(body);
        Assert.Equal(42, run(() => { trace.Add("prefix"); return 34; }, () => { trace.Add("size"); return 8; }));
        Assert.Equal(new[] { "prefix", "size" }, trace);
    }

    public enum SmallFlags : ushort { None = 0, One = 1, High = 0x8000 }

    [Fact]
    public void CloningKeepsTheNarrowTypeOfRewrittenBitwiseOperands()
    {
        var type = _modResolver.Import(typeof(SmallFlags));
        var body = Utils.CreateTestMethod(_modResolver, type, type, PrimType.Bool);
        var entry = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        var merge = body.CreateBlock();
        var value = new BinaryInst(BinaryOp.Or, body.Args[0], body.Args[0]);
        entry.InsertLast(value);
        value.Right = ConstInt.CreateI(1);
        entry.SetBranch(new BranchInst(body.Args[1], left, right));
        left.SetBranch(merge);
        right.SetBranch(merge);
        var phi = merge.InsertPhi(new PhiInst(type, (left, value), (right, body.Args[0])));
        merge.SetBranch(new ReturnInst(phi));
        var run = Utils.Compile<Func<SmallFlags, bool, SmallFlags>>(body);
        Assert.Equal(SmallFlags.High | SmallFlags.One, run(SmallFlags.High, true));
        Assert.Equal(SmallFlags.High, run(SmallFlags.High, false));
    }

    [Fact]
    public void TypedReferenceTypeTokensRoundTrip()
    {
        var body = Utils.CreateTestMethod(_modResolver, _modResolver.SysTypes.RuntimeTypeHandle, PrimType.TypedRef);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.Emit(new CilIntrinsic.RefAnyType(_modResolver, body.Args[0])));
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        var imported = Cilica.Frontend.ILImporter.ParseCode(body.Definition);
        body.Definition.Body = imported;
        var run = Utils.Compile<TypeToken>(imported);
        int number = 42;
        Assert.Equal(typeof(int).TypeHandle, run(__makeref(number)));
        string text = "text";
        Assert.Equal(typeof(string).TypeHandle, run(__makeref(text)));
    }

    delegate RuntimeTypeHandle TypeToken(TypedReference reference);

    [Fact]
    public void PreparingPhiInputsDoesNotMoveAnExceptionGuard()
    {
        var exceptionType = _modResolver.Import(typeof(Exception));
        var callbackType = _modResolver.Import(typeof(Action));
        var body = Utils.CreateTestMethod(_modResolver, exceptionType, callbackType);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var normal = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        var guard = new GuardInst(GuardKind.Catch, handler, exceptionType);
        guarded.InsertLast(guard);
        new IRBuilder(guarded, InsertionDir.After).CreateCallVirt(callbackType.FindMethod("Invoke"), body.Args[0]);
        guarded.SetBranch(normal);
        normal.SetBranch(new LeaveInst(exit));
        handler.SetBranch(new LeaveInst(exit));
        var phi = exit.InsertPhi(new PhiInst(exceptionType, (normal, ConstNull.Create()), (handler, guard)));
        exit.SetBranch(new ReturnInst(phi));
        var run = Utils.Compile<Func<Action, Exception?>>(body);
        Assert.Null(run(() => { }));
        var exception = new InvalidOperationException();
        Assert.Same(exception, run(() => throw exception));
        Assert.Same(guard, guarded.First);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinallyResumesWithMultiplePhiTargetsRemainExecutable(bool fromFinally)
    {
        var callbackType = _modResolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool, callbackType);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        var handler = body.CreateBlock();
        var exitLeft = body.CreateBlock();
        var exitRight = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Finally, handler));
        guarded.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(new LeaveInst(exitLeft));
        right.SetBranch(new LeaveInst(exitRight));
        var hb = new IRBuilder(handler);
        var result = hb.CreateCallVirt(callbackType.FindMethod("Invoke"), body.Args[1]);
        handler.SetBranch(new ResumeInst([exitLeft, exitRight]));
        var a = exitLeft.InsertPhi(new PhiInst(PrimType.Int32,
            (left, fromFinally ? new Undef(PrimType.Int32) : ConstInt.CreateI(17)),
            (handler, fromFinally ? result : ConstInt.CreateI(17))));
        var b = exitRight.InsertPhi(new PhiInst(PrimType.Int32,
            (right, fromFinally ? new Undef(PrimType.Int32) : ConstInt.CreateI(29)),
            (handler, fromFinally ? result : ConstInt.CreateI(29))));
        exitLeft.SetBranch(new ReturnInst(a));
        exitRight.SetBranch(new ReturnInst(b));
        IRVerifier.Verify(body);
        var run = Utils.Compile<Func<bool, Func<int>, int>>(body);
        int calls = 0;
        int Callback() { calls++; return 42; }
        Assert.Equal(fromFinally ? 42 : 17, run(true, Callback));
        Assert.Equal(fromFinally ? 42 : 29, run(false, Callback));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ForestPreservesExceptionOrderWithStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ib = new IRBuilder(body.CreateBlock());
        var division = ib.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(10), body.Args[0]));
        ib.CreateFieldStore(field, null, ConstInt.CreateI(1));
        ib.CreateReturn(division);
        var il = ILGenerator.GenerateCode(body).Instructions.ToList();

        Assert.True(il.FindIndex(i => i.OpCode == ILCode.Div) < il.FindIndex(i => i.OpCode == ILCode.Stsfld));
    }

    [Theory]
    [InlineData(0, 1, false, true)]
    [InlineData(-1, 1, false, false)]
    [InlineData(0, 2, false, false)]
    [InlineData(0, 1, true, false)]
    public void PointerIndexExtensionsRespectInductionBounds(int start, int step, bool unsignedBound, bool zeroExtend)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateByref(), PrimType.Int32);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateAccum(ConstInt.CreateI(start), current => loop.Latch.CreateAdd(current, ConstInt.CreateI(step)));

        loop.Build(header => header.CreateCmp(unsignedBound ? CompareOp.Ult : CompareOp.Slt, index, body.Args[1]), builder => {
            builder.CreateStore(builder.CreatePtrOffset(body.Args[0], index), index);
        });
        loop.Exit.CreateReturn();
        Run(body, new SimplifyCFG());
        var offset = Assert.Single(body.Instructions().OfType<PtrOffsetInst>());
        long revision = body.Revision;
        var il = ILGenerator.GenerateCode(body).Instructions;

        Assert.Contains(il, inst => inst.OpCode == (zeroExtend ? ILCode.Conv_U : ILCode.Conv_I));
        Assert.DoesNotContain(il, inst => inst.OpCode == (zeroExtend ? ILCode.Conv_I : ILCode.Conv_U));
        Assert.Same(index, offset.Index);
        Assert.Equal(revision, body.Revision);

        if (start == 0) {
            var values = Enumerable.Repeat(-1, 17).ToArray();
            Utils.Compile<FillElements>(body)(ref values[0], values.Length);
            Assert.Equal(Enumerable.Range(0, values.Length).Select(i => i % step == 0 ? i : -1), values);
        }
    }

    private delegate void FillElements(ref int first, int count);

    [Fact]
    public void ScalarArrayLoopsUseIndexedCilAndPreserveExceptions()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Int32.CreateArray(), PrimType.Int32);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        var abs = _modResolver.Import(typeof(Math)).FindMethod("Abs", new MethodSig(PrimType.Int32, [PrimType.Int32]));

        loop.Build(header => header.CreateSlt(index, header.CreateConvert(header.CreateArrayLen(body.Args[0]), PrimType.Int32)), builder => {
            var value = builder.CreateCall(abs, builder.CreateAdd(index, body.Args[1]));
            builder.CreateArrayStore(body.Args[0], index, value);
        });
        loop.Exit.CreateReturn();
        Run(body, new SimplifyCFG(), new LoopStrengthReduction());
        long revision = body.Revision;
        var il = ILGenerator.GenerateCode(body).Instructions;

        Assert.Contains(il, inst => inst.OpCode == ILCode.Stelem_I4);
        Assert.DoesNotContain(il, inst => inst.OpCode == ILCode.Stind_I4);
        Assert.Equal(revision, body.Revision);
        Assert.Single(body.Instructions().OfType<PtrOffsetInst>());
        var run = Utils.Compile<Action<int[], int>>(body);

        foreach (int count in new[] { 0, 1, 7, 16, 33 }) {
            var values = new int[count];
            run(values, -5);
            Assert.Equal(Enumerable.Range(0, count).Select(i => Math.Abs(i - 5)), values);
        }

        Assert.Throws<NullReferenceException>(() => run(null!, 0));
        var overflow = new int[3];
        Assert.Throws<OverflowException>(() => run(overflow, int.MaxValue));
        Assert.Equal(new[] { int.MaxValue, 0, 0 }, overflow);
    }

    [Theory]
    [InlineData("unknown-bound")]
    [InlineData("different-array")]
    [InlineData("reinterpretation")]
    [InlineData("volatile")]
    [InlineData("unaligned")]
    [InlineData("reference-store")]
    public void ArrayInstructionSelectionRequiresStorageAndAccessProofs(string scenario)
    {
        var element = scenario == "reference-store" ? PrimType.Object : PrimType.Int32;
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, element.CreateArray(), element.CreateArray(), PrimType.Int32);
        var entry = body.CreateBlock();
        var setup = new IRBuilder(entry);
        var (data, length) = LoopStrengthReduction.CreateGetDataPtrRange(setup, body.Args[0]);
        var bound = scenario switch {
            "unknown-bound" => body.Args[2],
            "different-array" => setup.CreateConvert(setup.CreateArrayLen(body.Args[1]), PrimType.Int32),
            _ => setup.CreateConvert(length, PrimType.Int32)
        };
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        var flags = scenario switch {
            "volatile" => PointerFlags.Volatile,
            "unaligned" => PointerFlags.Unaligned,
            _ => PointerFlags.None
        };

        loop.Build(header => header.CreateSlt(index, bound), builder => {
            Value value = scenario == "reference-store" ? ConstNull.Create() : index;
            var accessType = scenario == "reinterpretation" ? PrimType.Byte : element;
            builder.Emit(new StoreInst(builder.CreatePtrOffset(data, index), value, accessType, flags));
        });
        loop.Exit.CreateReturn();
        Run(body, new SimplifyCFG());
        var il = ILGenerator.GenerateCode(body).Instructions;

        Assert.DoesNotContain(il, inst => inst.OpCode is ILCode.Stelem or ILCode.Stelem_I4 or ILCode.Stelem_Ref or ILCode.Stelem_I1);
        Assert.Contains(il, inst => inst.OpCode is ILCode.Stind_I4 or ILCode.Stind_Ref or ILCode.Stind_I1);

        if (flags != PointerFlags.None) {
            Assert.Contains(il, inst => inst.OpCode == (flags == PointerFlags.Volatile ? ILCode.Volatile_ : ILCode.Unaligned_));
        }
    }

    [Fact]
    public void UnboundedPointerIndicesRetainNegativeOffsets()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.IntPtr, PrimType.IntPtr, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var address = builder.Emit(new PtrOffsetInst(body.Args[0], body.Args[1], stride: 4));
        builder.CreateReturn(builder.CreateConvert(address, PrimType.IntPtr));
        var run = Utils.Compile<Func<nint, int, nint>>(body);

        Assert.Equal((nint)96, run(100, -1));
        Assert.Equal((nint)108, run(100, 2));
    }

    [Theory]
    [InlineData(CompareOp.FOne, false)]
    [InlineData(CompareOp.FOne, true)]
    [InlineData(CompareOp.FUeq, false)]
    [InlineData(CompareOp.FUeq, true)]
    public void LegalizedFloatPredicatesPreserveNaNsAndSignedZeros(CompareOp operation, bool branch)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Double, PrimType.Double);
        var builder = new IRBuilder(body.CreateBlock());
        var comparison = builder.CreateCmp(operation, body.Args[0], body.Args[1]);

        if (branch) {
            var yes = body.CreateBlock();
            var no = body.CreateBlock();
            builder.SetBranch(comparison, yes, no);
            yes.SetBranch(new ReturnInst(ConstInt.CreateI(1)));
            no.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        } else {
            builder.CreateReturn(comparison);
        }

        long revision = body.Revision;
        var run = Utils.Compile<Func<double, double, bool>>(body);
        double[] values = [double.NaN, double.NegativeInfinity, -1, -0.0, 0.0, 1, double.PositiveInfinity];

        foreach (double left in values) {
            foreach (double right in values) {
                bool orderedNotEqual = left < right || left > right;

                Assert.Equal(operation == CompareOp.FOne ? orderedNotEqual : !orderedNotEqual, run(left, right));
            }
        }

        Assert.Equal(revision, body.Revision);
    }

    [Fact]
    public void CodeGenerationPreservesTheSourceGraphAndIsRepeatable()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var other = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], exit, other));
        other.SetBranch(exit);
        var phi = exit.InsertPhi(new PhiInst(PrimType.Int32, (entry, ConstInt.CreateI(1)), (other, ConstInt.CreateI(2))));
        exit.SetBranch(new ReturnInst(phi));
        long revision = body.Revision;
        var first = ILGenerator.GenerateCode(body);
        var second = ILGenerator.GenerateCode(body);

        Assert.Equal(first.Instructions.Select(i => i.ToString()), second.Instructions.Select(i => i.ToString()));
        Assert.Equal(revision, body.Revision);
        Assert.Equal(3, body.NumBlocks);

        var run = Utils.Compile<Func<bool, int>>(body);

        Assert.Equal(1, run(true));
        Assert.Equal(2, run(false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PhiSchedulingPreservesReadsBeforeStores(bool expression, bool initLocals)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        body.InitLocals = initLocals;
        var entry = body.CreateBlock();
        var exit = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var slot = body.CreateVar(PrimType.Int32);
        builder.CreateStore(slot, ConstInt.CreateI(1));
        Value value = builder.CreateLoad(slot);

        if (expression) {
            value = builder.CreateAdd(value, ConstInt.CreateI(1));
        }

        builder.CreateStore(slot, ConstInt.CreateI(2));
        builder.SetBranch(exit);
        exit.SetBranch(new ReturnInst(exit.InsertPhi(new PhiInst(PrimType.Int32, (entry, value)))));

        Assert.Equal(initLocals, ILGenerator.GenerateCode(body).InitLocals);
        Assert.Equal(expression ? 2 : 1, Utils.Compile<Func<int>>(body)());
    }
}
