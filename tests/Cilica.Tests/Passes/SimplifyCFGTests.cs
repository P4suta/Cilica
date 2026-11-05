namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class SimplifyCFGTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_SimplifyCFG() => CheckIR("SimplifyCFG.ethil", new SimplifyCFG());

    [Fact]
    public void BooleanValuedLongReturnsKeepTheirStackWidth()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int64, PrimType.Int32);
        var entry = body.CreateBlock();
        var yes = body.CreateBlock();
        var no = body.CreateBlock();
        var condition = new IRBuilder(entry).CreateSgt(body.Args[0], ConstInt.CreateI(0));
        entry.SetBranch(new BranchInst(condition, yes, no));
        yes.SetBranch(new ReturnInst(ConstInt.CreateL(1)));
        no.SetBranch(new ReturnInst(ConstInt.CreateL(0)));
        Run(body, new SimplifyCFG());
        var run = Utils.Compile<Func<int, long>>(body);
        Assert.Equal(0L, run(-1));
        Assert.Equal(1L, run(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JumpThreadingRetainsInfiniteLoopTargets(bool twoBlocks)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var loop = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], loop, exit));
        if (twoBlocks) {
            var backedge = body.CreateBlock();
            loop.SetBranch(backedge);
            backedge.SetBranch(loop);
        } else {
            loop.SetBranch(loop);
        }
        exit.SetBranch(new ReturnInst(ConstInt.CreateI(42)));

        Run(body, new SimplifyCFG(), new DeadCodeElim());
        Assert.Contains(body.Instructions().OfType<BranchInst>(), branch => branch.IsJump && branch.Then == branch.Block);
        Assert.Equal(42, Utils.Compile<Func<bool, int>>(body)(false));
    }

    [Fact]
    public void TailCallsKeepTheirImmediateReturns()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var target = body.Definition.DeclaringType.CreateMethod("Target", PrimType.Int32, [],
            MethodAttributes.Public | MethodAttributes.Static);
        target.Body = new MethodBody(target);
        target.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var entry = body.CreateBlock();
        var tail = body.CreateBlock();
        var other = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], tail, other));
        var call = new IRBuilder(tail).Emit(new CallInst(target, []) { IsTailCall = true });
        tail.SetBranch(new ReturnInst(call));
        other.SetBranch(new ReturnInst(ConstInt.CreateI(7)));

        Run(body, new SimplifyCFG());
        Assert.IsType<ReturnInst>(call.Next);
        var run = Utils.Compile<Func<bool, int>>(body);
        Assert.Equal(42, run(true));
        Assert.Equal(7, run(false));
    }

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
