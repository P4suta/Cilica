namespace DistIL.Tests.Analysis;

using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.IR;
using DistIL.Util;

public class LoopAnalysisTests
{
    [Fact]
    public void NestedLoopsHaveParentsAndDistinctInnermostLoops()
    {
        var body = Utils.CreateDummyMethodBody(PrimType.Void, PrimType.Bool, PrimType.Bool);
        var entry = body.CreateBlock();
        var outer = body.CreateBlock();
        var inner = body.CreateBlock();
        var innerLatch = body.CreateBlock();
        var outerLatch = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(outer);
        outer.SetBranch(new BranchInst(body.Args[0], inner, exit));
        inner.SetBranch(new BranchInst(body.Args[1], innerLatch, outerLatch));
        innerLatch.SetBranch(inner);
        outerLatch.SetBranch(outer);
        exit.SetBranch(new ReturnInst());
        body.CreateBlock().SetBranch(new ReturnInst());
        var analysis = new LoopAnalysis(body, new DominatorTree(body));
        var root = Assert.Single(analysis.RootLoops);
        var nested = Assert.Single(analysis.GetInnermostLoops());

        Assert.Same(outer, root.Header);
        Assert.Same(inner, nested.Header);
        Assert.Same(root, nested.Parent);
        Assert.Same(nested, Assert.Single(root.GetChildren()));
    }

    [Fact]
    public void BackedgesWithTheSameHeaderBelongToOneLoop()
    {
        var body = Utils.CreateDummyMethodBody(PrimType.Void, PrimType.Bool);
        var entry = body.CreateBlock();
        var header = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        entry.SetBranch(header);
        header.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(header);
        right.SetBranch(header);
        var loop = Assert.Single(new LoopAnalysis(body, new DominatorTree(body)).Loops);

        Assert.Equal(3, loop.NumBlocks);
        Assert.Null(loop.GetLatch());
        Assert.Same(entry, loop.GetPreheader());
    }
}
