namespace DistIL.Tests.CodeGen;

using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
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
