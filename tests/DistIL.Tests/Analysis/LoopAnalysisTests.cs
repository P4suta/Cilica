namespace DistIL.Tests.Analysis;

using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Util;

[Collection("ModuleResolver")]
public class LoopAnalysisTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void EnumeratorTripCountsRequireStableStorageAndExclusiveAdvancement(bool array, bool extraAdvance, bool expected)
    {
        var sourceType = array ? (TypeDesc)PrimType.Int32.CreateArray() : fixture.Resolver.Import(typeof(IReadOnlyCollection<int>));
        var method = Utils.CreateTestMethod(fixture.Resolver, PrimType.Void, sourceType);
        var entry = method.CreateBlock();
        var header = method.CreateBlock();
        var body = method.CreateBlock();
        var exit = method.CreateBlock();
        var start = new IRBuilder(entry);
        var iterator = start.CreateCallVirt(fixture.Resolver.Import(typeof(IEnumerable<int>)).FindMethod("GetEnumerator"), method.Args[0]);
        start.SetBranch(header);
        var head = new IRBuilder(header);
        head.SetBranch(head.CreateCallVirt("MoveNext", iterator), body, exit);
        var items = new IRBuilder(body);
        items.CreateCallVirt("get_Current", iterator);

        if (extraAdvance) {
            items.CreateCallVirt("MoveNext", iterator);
        }

        items.SetBranch(header);
        exit.SetBranch(new ReturnInst());
        var dom = new DominatorTree(method);
        var loop = Assert.Single(new LoopAnalysis(method, dom).Loops);
        var shape = ShapedLoopInfo.Parse(loop);

        Assert.Equal(expected, shape?.HasKnownTripCount(dom, entry.Last) == true);
    }

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
