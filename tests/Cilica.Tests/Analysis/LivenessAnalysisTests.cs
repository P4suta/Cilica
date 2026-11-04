namespace Cilica.Tests.Analysis;

using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.IR;

public class LivenessAnalysisTests
{
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
