namespace DistIL.Passes;

using System.Reflection;
using MethodBody = IR.MethodBody;

using DistIL.IR.Utils;
using DistIL.Analysis;

public class InlineMethods : IMethodPass
{
    readonly Options _opts;

    public InlineMethods(Options? opts = null)
    {
        _opts = opts ?? new();
    }

    static IMethodPass IMethodPass.Create<TSelf>(Compilation comp)
        => new InlineMethods();

    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var worklist = new Queue<(CallInst Call, ImmutableArray<MethodDef> Ancestors)>();

        // Find initial calls
        foreach (var inst in ctx.Method.Instructions()) {
            if (inst is CallInst call) {
                worklist.Enqueue((call, [ctx.Definition]));
            }
        }

        var advisor = ctx.Compilation.GetAnalysis<InliningAdvisor>();
        var ownMetrics = advisor.GetMetrics(ctx.Method).Metrics;

        // Budget should be less than caller cost to avoid code duplication (eg. in forwarding methods).
        int budget = _opts.InitialBudget + (int)(ownMetrics == null ? 0 : ownMetrics.BaseCost * _opts.BudgetFactor);
        int numChanges = 0;
        int expandedInstructions = 0;

        // Recursive inline
        while (worklist.TryDequeue(out var item) && budget > 0 && numChanges < _opts.MaxExpansions) {
            var (call, ancestors) = item;

            if (call.Block == null || ancestors.Length >= _opts.MaxDepth) {
                continue;
            }

            var target = ResolveTarget(ctx, advisor, call);

            if (target == null || ancestors.Contains(target)) {
                continue;
            }

            int cost = advisor.EvaluateInliningCost(target.Body!, call.Args);
            if (cost > _opts.CostThreshold || cost > budget) continue;

            int instructionCount = target.Body!.Instructions().Count();

            if (instructionCount > _opts.MaxExpandedInstructions - expandedInstructions) {
                continue;
            }

            expandedInstructions += instructionCount;

            budget -= Math.Max(cost, -_opts.NegativeCostLimit);

            ctx.Logger.Trace($"Inlining '{target}' into '{ctx.Method}'. RemBudget={budget} Cost={cost}");

            var chain = ancestors.Add(target);
            var result = Inline(call, (MethodDefOrSpec)call.Method, call.Args, ctx.Compilation,
                onNewCall: nested => worklist.Enqueue((nested, chain)));

            if (result != null) {
                call.ReplaceUses(result);
            }
            call.Remove();
            
            numChanges++;
        }

        if (numChanges > 0) {
            advisor.InvalidateMetrics(ctx.Method);
            return MethodInvalidations.Everything;
        }
        return MethodInvalidations.None;
    }

    private static MethodDef? ResolveTarget(MethodTransformContext ctx, InliningAdvisor advisor, CallInst call)
    {
        // Must be a non-recursive MethodDef
        var parent = ctx.Method.Definition;

        if (call.Method is not MethodDefOrSpec { Definition: var target } || target == parent) {
            return null;
        }

        if (ctx.Compilation.HasFailed(target)) {
            return null;
        }

        // TODO: support for static virtuals
        if (target.Attribs.HasFlag(MethodAttributes.Static | MethodAttributes.Virtual)) {
            return null;
        }

        // Known virtual target
        if (call.IsVirtual && target.Attribs.HasFlag(MethodAttributes.Virtual) && !target.Attribs.HasFlag(MethodAttributes.Final)) {
            if (TypeUtils.ResolveVirtualMethod(call.Method, call.Args[0]) is not MethodDefOrSpec actualTarget || actualTarget == parent) {
                return null;
            }
            call.Method = actualTarget;
            target = actualTarget.Definition;
        }

        if (advisor.EarlyCheck(target) != InlineRejectReason.Accepted) {
            return null;
        }

        // If there's no body after devirt, there's no way we can inline this method.
        if (target.Body == null && !advisor.ImportBodyForInlining(target)) {
            return null;
        }
        // CIL filters cannot contain nested exception regions. Keep a call to an
        // EH-bearing method here instead of cloning its try/handler into a filter.
        if (target.Body!.Instructions().Any(inst => inst is GuardInst)) {
            var region = new ProtectedRegionAnalysis(ctx.Method).GetBlockRegion(call.Block);
            if (region.GetHandlerGuard() is { HasFilter: true } guard && guard.FilterBlock == region.StartBlock) {
                return null;
            }
        }
        return target;
    }

    public static Value? Inline(Instruction call, MethodDefOrSpec target, ReadOnlySpan<Value> args, Compilation comp, Action<CallInst>? onNewCall = null)
    {
        var callerBody = call.Block.Method;
        var targetBody = Ensure.NotNull(target.Definition.Body);
        Ensure.That(callerBody != targetBody, "Cannot inline method into itself");

        var builder = new IRBuilder(call, InsertionDir.Before);
        if (call is CallInst { IsVirtual: true, InBounds: false } && args[0].ResultType.IsManagedObject()) {
            builder.CreateNullCheck(args[0]);
        }
        if ((target.IsStatic || call is NewObjInst) && target.DeclaringType.RequiresTypeInitialization()) {
            builder.CreateTypeInitialization(target.DeclaringType);
        }

        var cloner = new InlinerIRCloner(callerBody, target, onNewCall);

        // Add argument mappings
        for (int i = 0; i < targetBody.Args.Length; i++) {
            var arg = targetBody.Args[i];
            var val = StoreInst.Coerce(arg.ResultType, args[i], insertBefore: call);
            cloner.AddMapping(arg, val);
        }

        // Clone blocks
        var returningBlocks = new List<BasicBlock>();
        var lastBlock = call.Block;

        foreach (var block in targetBody) {
            var newBlock = callerBody.CreateBlock(insertAfter: lastBlock);
            cloner.AddMapping(block, newBlock);
            lastBlock = newBlock;

            if (block.Last is ReturnInst) {
                returningBlocks.Add(newBlock);
            }
        }

        cloner.Run(targetBody.EntryBlock);
        returningBlocks.RemoveAll(cloner.IsDead);

        // If the target only has a single block ending with return,
        // the entire code can be moved at once without creating new blocks.
        var result = targetBody.NumBlocks == 1 && returningBlocks.Count == 1
            ? InlineOneBlock(call, returningBlocks[0])
            : InlineManyBlocks(call, cloner.GetMapping(targetBody.EntryBlock), returningBlocks);

        if (result != null) {
            // After inlining, `call` will be at the start of the continuation block.
            return result;
        } else if (target.ReturnType != PrimType.Void) {
            // This could happen if we inline a method that ends with ThrowInst, but it still returns something.
            // Code after `call` should be unreachable now, but we need to replace its uses with undef to avoid making the IR invalid.
            return new Undef(call.ResultType);
        }
        return null;
    }

    private static Value? InlineOneBlock(Instruction call, BasicBlock block)
    {
        var ret = (ReturnInst)block.Last;

        // Move code (if not a single return)
        if (ret.Prev != null) {
            block.MoveRange(call.Block, call.Prev, block.First, ret.Prev);
        }
        block.Remove();
        
        if (ret.Value != null) {
            return StoreInst.Coerce(call.ResultType, ret.Value, insertBefore: call);
        }
        return null;
    }

    private static Value? InlineManyBlocks(Instruction call, BasicBlock entryBlock, List<BasicBlock> returningBlocks)
    {
        var startBlock = call.Block;
        var endBlock = startBlock.Split(call, branchTo: entryBlock);

        var results = new PhiArg[returningBlocks.Count];
        int resultIdx = 0;

        // Rewrite exit blocks to jump into endBlock
        foreach (var block in returningBlocks) {
            if (block.Last is ReturnInst { HasValue: true } ret) {
                var result = StoreInst.Coerce(call.ResultType, ret.Value, insertBefore: ret);
                results[resultIdx++] = (block, result);
            }
            block.SetBranch(endBlock);
        }

        // Return the generated value
        if (resultIdx >= 2) {
            return endBlock.InsertPhi(new PhiInst(call.ResultType, results));
        }
        return results.FirstOrDefault().Value;
    }

    sealed class InlinerIRCloner(MethodBody caller, MethodDefOrSpec target, Action<CallInst>? onNewCall)
        : IRCloner(caller, new GenericContext(target))
    {
        protected override Value CreateClone(Instruction inst)
        {
            var clonedVal = base.CreateClone(inst);

            if (onNewCall != null && clonedVal is CallInst { Method: MethodDefOrSpec method } call) {
                // Ignore calls to recursive methods to prevent infinite loop.
                if (method.Definition != target.Definition) {
                    onNewCall.Invoke(call);
                }
            }
            return clonedVal;
        }
    }

    public class Options
    {
        public int MaxDepth { get; init; } = 16;

        public int MaxExpansions { get; init; } = 128;

        public int MaxExpandedInstructions { get; init; } = 4096;

        /// <summary> Inline candidate instruction limit. </summary>
        public int CostThreshold { get; init; } = 250;

        /// <summary> Initial inlining budget. </summary>
        public int InitialBudget { get; init; } = 80;

        /// <summary> A factor used to derive inlining budget from method's own cost. </summary>
        public double BudgetFactor { get; init; } = 0.5;

        /// <summary> For inlinees with negative cost, sets the limit to increase inlining budget. </summary>
        public int NegativeCostLimit { get; init; } = 50;
    }
}
