namespace Cilica.Tests.Analysis;

using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.IR;

public class LivenessAnalysisTests
{
    [Fact]
    public void VariableLivenessHandlesMoreThan256Locals()
    {
        var method = Utils.CreateDummyMethodBody();
        var entry = method.CreateBlock();
        var bridge = method.CreateBlock();
        var exit = method.CreateBlock();
        var slots = Enumerable.Range(0, 257).Select(_ => method.CreateVar(PrimType.Int32)).ToArray();

        for (int i = 0; i < slots.Length; i++) {
            if (i % 2 == 0) entry.InsertLast(new StoreInst(slots[i], ConstInt.CreateI(i)));
            exit.InsertLast(new LoadInst(slots[i]));
        }
        entry.SetBranch(bridge);
        bridge.SetBranch(exit);
        exit.SetBranch(new ReturnInst());

        var analysis = new VarLivenessAnalysis(method);
        for (int i = 0; i < slots.Length; i++) {
            Assert.Equal(i % 2 != 0, analysis.GetLiveIn(entry).Contains(slots[i]));
            Assert.True(analysis.GetLiveOut(entry).Contains(slots[i]));
            Assert.True(analysis.GetLiveIn(bridge).Contains(slots[i]));
            Assert.True(analysis.GetLiveOut(bridge).Contains(slots[i]));
            Assert.True(analysis.GetLiveIn(exit).Contains(slots[i]));
            Assert.False(analysis.GetLiveOut(exit).Contains(slots[i]));
        }
    }

    [Fact]
    public void PhiEdgeUsesDoNotHideOrdinaryUsesInTheMergeBlock()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32, PrimType.Int32, PrimType.Bool);
        var entry = method.CreateBlock();
        var left = method.CreateBlock();
        var right = method.CreateBlock();
        var merge = method.CreateBlock();
        var value = new BinaryInst(BinaryOp.Add, method.Args[0], ConstInt.CreateI(1));
        entry.InsertLast(value);
        entry.SetBranch(new BranchInst(method.Args[1], left, right));
        left.SetBranch(merge);
        var other = new BinaryInst(BinaryOp.Add, method.Args[0], ConstInt.CreateI(2));
        right.InsertLast(other);
        right.SetBranch(merge);
        var phi = merge.InsertPhi(PrimType.Int32);
        var sum = new BinaryInst(BinaryOp.Add, value, phi);
        merge.InsertLast(sum);
        merge.SetBranch(new ReturnInst(sum));
        // Add the edge use last so it is visited before the ordinary use.
        phi.AddArg((left, value), (right, other));

        var analysis = new LivenessAnalysis(method);
        Assert.True(analysis.IsLiveIn(merge, value));
        Assert.True(analysis.IsLiveOut(left, value));
        Assert.True(analysis.IsLiveOut(right, value));
    }

    [Fact]
    public void VariableLivenessIgnoresUnreachablePredecessors()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var slot = method.CreateVar(PrimType.Int32);
        var entry = method.CreateBlock();
        var exit = method.CreateBlock();
        var dead = method.CreateBlock();
        entry.InsertLast(new StoreInst(slot, ConstInt.CreateI(1)));
        entry.SetBranch(exit);
        var load = new LoadInst(slot);
        exit.InsertLast(load);
        exit.SetBranch(new ReturnInst(load));
        dead.SetBranch(exit);
        var analysis = new VarLivenessAnalysis(method);

        Assert.True(analysis.GetLiveOut(entry).Contains(slot));
        Assert.True(analysis.GetLiveIn(exit).Contains(slot));
        Assert.False(analysis.GetLiveIn(entry).Contains(slot));
    }
}
