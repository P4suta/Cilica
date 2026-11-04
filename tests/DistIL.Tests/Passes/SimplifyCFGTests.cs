namespace DistIL.Tests.Passes;

using System.Reflection;
using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;
using MethodBody = DistIL.IR.MethodBody;

[Collection("ModuleResolver")]
public class SimplifyCFGTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_SimplifyCFG() => CheckIR("SimplifyCFG.ethil", new SimplifyCFG());

    [Fact]
    public void JumpThreadingCanReplaceAnAncestorsTerminator()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var side = body.CreateBlock();
        var merge = body.CreateBlock();
        var yes = body.CreateBlock();
        var no = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], side, merge));
        side.SetBranch(merge);
        var phi = merge.InsertPhi(new PhiInst(PrimType.Bool, (entry, ConstInt.CreateI(0)), (side, ConstInt.CreateI(1))));
        merge.SetBranch(new BranchInst(phi, yes, no));
        yes.SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        no.SetBranch(new ReturnInst(ConstInt.CreateI(7)));

        Run(body, new SimplifyCFG(), new DeadCodeElim());
        var run = Utils.Compile<Func<bool, int>>(body);
        Assert.Equal(42, run(true));
        Assert.Equal(7, run(false));
    }

    [Fact]
    public void MergedReturnsCoerceSmallIntegerValues()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Byte, PrimType.Int32);
        var entry = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(new ReturnInst(body.Args[0]));
        right.SetBranch(new ReturnInst(ConstInt.CreateI(42)));

        Run(body, new SimplifyCFG());
        var run = Utils.Compile<Func<int, byte>>(body);
        Assert.Equal((byte)42, run(0));
        Assert.Equal((byte)255, run(-1));
        Assert.Equal((byte)1, run(257));
    }

    [Fact]
    public void SimplifyCfgStoresSinglePrecisionSwitchBits()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Single, PrimType.Int32);
        var entry = body.CreateBlock();
        var a = body.CreateBlock();
        var b = body.CreateBlock();
        var fallback = body.CreateBlock();
        var merge = body.CreateBlock();
        entry.SetBranch(new SwitchInst(body.Args[0], fallback, a, b));
        a.SetBranch(merge);
        b.SetBranch(merge);
        fallback.SetBranch(merge);
        var phi = merge.InsertPhi(new PhiInst(PrimType.Single, (a, ConstFloat.CreateS(1)), (b, ConstFloat.CreateS(2)), (fallback, ConstFloat.CreateS(-0f))));
        merge.SetBranch(new ReturnInst(phi));
        Run(body, new SimplifyCFG());
        var run = Utils.Compile<Func<int, float>>(body);

        Assert.Equal(1, run(0));
        Assert.Equal(2, run(1));
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(run(-1)));
    }
}
