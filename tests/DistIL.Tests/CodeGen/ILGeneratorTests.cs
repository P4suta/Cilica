namespace DistIL.Tests.CodeGen;

using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Tests.Passes;
using System.Reflection;

[Collection("ModuleResolver")]
public class ILGeneratorTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_ILGenerator() => CheckCIL("CodeGen/StructFieldInsertExtract.ethil");

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
