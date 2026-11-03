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

    [Fact]
    public void EffectInvalidationPreservesUnrelatedSummaries()
    {
        var ctx = CreateContext();
        var type = ctx.Definition.DeclaringType;
        var leaf = type.CreateMethod("Leaf", PrimType.Int32, [], System.Reflection.MethodAttributes.Static);
        leaf.Body = new MethodBody(leaf);
        leaf.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(1)));
        var caller = type.CreateMethod("Caller", PrimType.Int32, [], System.Reflection.MethodAttributes.Static);
        caller.Body = new MethodBody(caller);
        var builder = new IRBuilder(caller.Body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(leaf));
        var effects = ctx.Compilation.GetAnalysis<GlobalFunctionEffects>();

        Assert.True(effects.GetEffects(caller).IsPure);
        Assert.True(effects.GetEffects(ctx.Definition).IsPure);

        long computations = effects.Computations;
        var loop = leaf.Body.CreateBlock();
        loop.SetBranch(loop);
        leaf.Body.EntryBlock.SetBranch(loop);
        ctx.Compilation.InvalidateAnalyses(leaf);

        Assert.True(effects.GetEffects(ctx.Definition).IsPure);
        Assert.Equal(computations, effects.Computations);
        Assert.True(effects.GetEffects(caller).MayDiverge);
    }

    [Fact]
    public void InliningMetricsFollowMethodRevisions()
    {
        var ctx = CreateContext();
        var advisor = ctx.Compilation.GetAnalysis<InliningAdvisor>();
        var metrics = advisor.GetMetrics(ctx.Method).Metrics;
        var extra = new BinaryInst(BinaryOp.Add, ctx.Method.Args[0], ConstInt.CreateI(3));
        ctx.Method.EntryBlock.InsertAnteLast(extra);

        Assert.NotSame(metrics, advisor.GetMetrics(ctx.Method).Metrics);
    }
}
