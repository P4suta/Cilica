namespace DistIL.Tests.Analysis;

using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;

[Collection("ModuleResolver")]
public class AnalysisManagerTests(ModuleResolverFixture fixture)
{
    MethodTransformContext CreateContext()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(body.Args[0], ConstInt.CreateI(1)));

        return new MethodTransformContext(new Compilation(body.Definition.Module, new VoidLogger(), new()), body);
    }

    [Fact]
    public void DependencyConstructionPreservesCacheEntries()
    {
        var ctx = CreateContext();
        var graph = ctx.GetAnalysis<InterferenceGraph>();

        Assert.Same(graph, ctx.GetAnalysis<InterferenceGraph>());
        Assert.Equal(4, ctx.AnalysisBuilds);

        ctx.Invalidate<LivenessAnalysis>();

        Assert.NotSame(graph, ctx.GetAnalysis<InterferenceGraph>());
    }

    [Fact]
    public void MutationInvalidatesDependentAnalyses()
    {
        var ctx = CreateContext();
        var dom = ctx.GetAnalysis<DominatorTree>();
        var graph = ctx.GetAnalysis<InterferenceGraph>();
        var value = (BinaryInst)ctx.Method.EntryBlock.First;
        value.Right = ConstInt.CreateI(3);

        Assert.Same(dom, ctx.GetAnalysis<DominatorTree>());
        Assert.NotSame(graph, ctx.GetAnalysis<InterferenceGraph>());

        var exit = ctx.Method.CreateBlock();
        exit.SetBranch(new ReturnInst(value));
        ctx.Method.EntryBlock.SetBranch(exit);

        Assert.NotSame(dom, ctx.GetAnalysis<DominatorTree>());
    }

    [Fact]
    public void CyclesProduceDiagnosticsAndAllowSubsequentQueries()
    {
        var ctx = CreateContext();
        var error = Assert.Throws<InvalidOperationException>(() => ctx.GetAnalysis<CyclicAnalysis>());

        Assert.Contains("CyclicAnalysis", error.Message);
        Assert.Same(ctx.GetAnalysis<DominatorTree>(), ctx.GetAnalysis<DominatorTree>());
    }

    sealed class CyclicAnalysis : IMethodAnalysis
    {
        static IMethodAnalysis IMethodAnalysis.Create(IMethodAnalysisManager mgr) => mgr.GetAnalysis<CyclicAnalysis>();
    }
}
