namespace DistIL.Passes;

using DistIL.IR.Utils;

/// <summary> Executes a method pass with change tracking, invalidation, verification, and inspection. </summary>
public static class PassRunner
{
    public static MethodPassResult Run(IMethodPass pass, MethodTransformContext ctx, ReadOnlySpan<IPassInspector> inspectors = default)
    {
        long revision = ctx.Method.Revision;
        long controlFlowRevision = ctx.Method.ControlFlowRevision;

        try {
            foreach (var inspector in inspectors) {
                inspector.OnBeforePass(pass, ctx);
            }

            var result = pass.Run(ctx);
            result = ctx.Method.Revision == revision ? MethodInvalidations.None
                : result.Changes | (ctx.Method.ControlFlowRevision != controlFlowRevision
                    ? MethodInvalidations.ControlFlow : MethodInvalidations.DataFlow);
            result.InvalidateAffectedAnalyses(ctx);

            if (ctx.Compilation.Settings.VerifyEachPass) {
                IRVerifier.Verify(ctx.Method);
            }

            foreach (var inspector in inspectors) {
                inspector.OnAfterPass(pass, ctx, result);
            }

            return result;
        } catch (Exception error) {
            ctx.InvalidateAll();
            ctx.Compilation.InvalidateAnalyses(ctx.Definition);

            foreach (var inspector in inspectors) {
                inspector.OnPassFailed(pass, ctx, error);
            }

            throw;
        }
    }
}
