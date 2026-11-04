namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.Passes.Vectorization;

public class LoopVectorizer : IMethodPass
{
    readonly VectorTranslator _trans;

    public LoopVectorizer(ModuleResolver resolver)
    {
        _trans = new VectorTranslator(resolver);
    }

    static IMethodPass IMethodPass.Create<TSelf>(Compilation comp)
        => new LoopVectorizer(comp.Resolver);

    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var loopAnalysis = ctx.GetAnalysis<LoopAnalysis>();
        bool changed = false;

        foreach (var loop in loopAnalysis.GetShapedLoops(innermostOnly: true)) {
            if (loop is not CountingLoopInfo forLoop) {
                continue;
            }

            if (VectorizationLegality.Check(forLoop, ctx, out string? reason)) {
                changed |= InnerLoopVectorizer.Run(forLoop, _trans, ctx.Logger);
            } else {
                ctx.Logger.Trace($"Loop vectorization declined: {reason}");
            }
        }

        return changed ? MethodInvalidations.Loops : MethodInvalidations.None;
    }
}
