namespace Cilica.Analysis;

using Cilica.Frontend;
using Cilica.Passes;
using Cilica.IR.Utils;

using MethodAttributes = System.Reflection.MethodAttributes;
using ParameterAttributes = System.Reflection.ParameterAttributes;
using MethodImplAttributes = System.Reflection.MethodImplAttributes;

public class InliningAdvisor : IGlobalAnalysis
{
    readonly Dictionary<MethodDef, Entry> _cache = new();
    readonly Compilation _comp;
    readonly HashSet<MethodDef> _importing = new();

    sealed record Entry(InlineRejectReason Reason, FunctionMetrics? Metrics, MethodBody Body, long Revision);

    private InliningAdvisor(Compilation comp) => _comp = comp;

    void IGlobalAnalysis.Invalidate(MethodDef method) => _cache.Remove(method);

    static IGlobalAnalysis IGlobalAnalysis.Create(Compilation comp)
        => new InliningAdvisor(comp);

    public int EvaluateInliningCost(MethodBody targetBody, ReadOnlySpan<Value> args)
    {
        var (rejection, metrics) = GetMetrics(targetBody);

        if (rejection != InlineRejectReason.Accepted) {
            return int.MaxValue;
        }

        // Loop duplication affects profitability, not the caller's available budget.
        int cost = metrics!.BaseCost + (metrics.HasLoops ? 30 : 0);
        int argumentBenefit = 0;
        int typeBenefit = 0;
        bool hasConstantArgs = false;

        for (int i = 0; i < targetBody.Args.Length; i++) {
            var argMetrics = metrics.Args[i];
            argumentBenefit += argMetrics.BenefitIfInlined;
            hasConstantArgs |= args[i] is Const;

            if (TypeUtils.HasConcreteType(args[i]) && !TypeUtils.HasConcreteType(targetBody.Args[i])) {
                typeBenefit += argMetrics.BenefitIfKnownType;
            }
        }
        // Passing and dispatch savings do not eliminate the expanded body.
        cost -= Math.Min(argumentBenefit, 12);
        cost -= Math.Min(typeBenefit, 80);
        if (hasConstantArgs) cost -= EvaluateConstantBenefit(targetBody, args);

        // Keep a profitability cost for hinted expansion without reducing folding benefits.
        return cost > 0 && targetBody.Definition.ImplAttribs.HasFlag(MethodImplAttributes.AggressiveInlining)
            ? cost / 3 : cost;
    }

    /// <summary> Gets or attempts to import a method body for IPO purposes. </summary>
    public MethodBody? GetMethodBodyForIPO(MethodDef method)
    {
        if (_comp.HasFailed(method) || _importing.Contains(method)) {
            return null;
        }

        if (method.Module != _comp.Module && !_comp.Settings.AllowCrossAssemblyIPO) {
            return null;
        }

        if (method.Body == null && method.ILBody != null) {
            _importing.Add(method);

            try {
                _comp.Logger.Debug($"Importing method for IPO: {method}");
                var body = ILImporter.ParseCode(method);
                CompilationPipeline.Normalize(new MethodTransformContext(_comp, body), inlineConstructors: method.IsConstructor);
                method.Body = body;
                _comp.InvalidateAnalyses(method);
            } catch (Exception ex) {
                method.Body = null;
                _comp.FailMethod(method);
                _comp.Logger.Error($"Failed to import method for IPO: {method}", ex);
            } finally {
                _importing.Remove(method);
            }
        }

        return method.Body;
    }

    public (InlineRejectReason Rejection, FunctionMetrics? Metrics) GetMetrics(MethodBody method)
    {
        if (!_cache.TryGetValue(method.Definition, out var entry) || entry.Body != method || entry.Revision != method.Revision) {
            var (reason, metrics) = ComputeMetrics(method);
            entry = new Entry(reason, metrics, method, method.Revision);
            _cache[method.Definition] = entry;
        }

        return (entry.Reason, entry.Metrics);
    }

    public void InvalidateMetrics(MethodBody method)
    {
        _cache.Remove(method.Definition);
    }

    public InlineRejectReason EarlyCheck(MethodDef method)
    {
        if (method.Module == _comp.Resolver.CoreLib || method.CallConv == CallConvention.VarArgs || _comp.Runtime.IsOptimizationIntrinsic(method) ||
            _comp.Settings.PreserveTrimmingDependencies && _comp.Runtime.IsTrimmingIntrinsic(method) ||
            method.Attribs.HasFlag(MethodAttributes.RequireSecObject)) return InlineRejectReason.Ineligible;

        // Feature substitutions and trimming dependencies attach to this metadata.
        if (HasTrimmingContract(method.GetCustomAttribs()) ||
            HasTrimmingContract(method.ReturnParam.GetCustomAttribs()) ||
            method.Params.Any(parameter => HasTrimmingContract(parameter.GetCustomAttribs())) ||
            method.GenericParams.Any(parameter => HasTrimmingContract(parameter.CustomAttribs)) ||
            HasTrimmingContract(method.DeclaringType.GetCustomAttribs()) ||
            method.DeclaringType.GenericParams.Any(parameter => HasTrimmingContract(parameter.CustomAttribs)) ||
            method.DeclaringType.Properties.Any(property => (property.Getter == method || property.Setter == method) && HasTrimmingContract(property.GetCustomAttribs()))) {
            return InlineRejectReason.Ineligible;
        }

        int maxInstrs = method.ImplAttribs.HasFlag(MethodImplAttributes.AggressiveInlining)
            ? 4096
            : 256;

        if (method.ILBody != null && method.ILBody.Instructions.Count > maxInstrs) {
            return InlineRejectReason.Overbudget;
        }

        if ((method.ImplAttribs & (MethodImplAttributes.NoInlining | MethodImplAttributes.NoOptimization | MethodImplAttributes.Synchronized)) != 0) {
            return InlineRejectReason.Ineligible;
        }

        if (method.Attribs.HasFlag(MethodAttributes.Abstract) || 
            method.Attribs.HasFlag(MethodAttributes.PinvokeImpl) ||
            method.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") ||
            method.HasCustomAttrib("System.Runtime.CompilerServices", "IntrinsicAttribute")
        ) {
            return InlineRejectReason.UnknownTarget;
        }

        // Don't bother inlining async methods because we don't gain anything
        // from doing so yet, and it might mess up stack traces.
        if (method.IsRuntimeAsync || method.HasCustomAttrib("System.Runtime.CompilerServices", "AsyncStateMachineAttribute")) {
            return InlineRejectReason.Ineligible;
        }

        return InlineRejectReason.Accepted;
    }

    private bool HasTrimmingContract(IList<CustomAttrib> attributes)
        => attributes.Any(attribute => (attribute.Type.Namespace, attribute.Type.Name) switch {
            ("System.Diagnostics.CodeAnalysis", "FeatureSwitchDefinitionAttribute" or "FeatureGuardAttribute") => true,
            ("System.Diagnostics.CodeAnalysis", "DynamicallyAccessedMembersAttribute" or "DynamicDependencyAttribute") or
            ("System.Runtime.CompilerServices", "PreserveDependencyAttribute") => _comp.Settings.PreserveTrimmingDependencies,
            _ => false
        });

    private (InlineRejectReason Reason, FunctionMetrics? Metrics) ComputeMetrics(MethodBody method)
    {
        // Early bail on methods with too many blocks
        if (method.NumBlocks >= 200) {
            return (InlineRejectReason.Overbudget, null);
        }

        var metrics = new FunctionMetrics {
            Args = new ArgumentMetrics[method.Args.Length]
        };
        int i = 0;
        int numReturns = 0;

        foreach (var arg in method.Args) {
            ref var argMetrics = ref metrics.Args[i++];

            UpdateArgumentUseInfo(ref argMetrics, arg, new HashSet<Instruction>());

            // Passing costs are a small bonus, independent of specialization.
            if (arg.ResultType is TypeDefOrSpec { IsValueType: true } type) {
                argMetrics.BenefitIfInlined += Math.Min(type.Definition.Fields.Count(field => field.IsInstance) * 2, 12);
            }
            // Inlining functions with `out var` may enable promotion of exposed local vars
            else if (arg.Param.Attribs.HasFlag(ParameterAttributes.Out)) {
                argMetrics.BenefitIfInlined += 8;
            }
            // First few arguments are passed in registers, rest on stack
            else {
                argMetrics.BenefitIfInlined += (i > 4 ? 2 : 1);
            }
        }

        foreach (var block in method) {
            // Penalize exception regions because they're complicated
            foreach (var guard in block.Guards()) {
                metrics.BaseCost += guard.Kind == GuardKind.Finally ? 30 : 150;
            }

            foreach (var inst in block.NonPhis()) {
                // Cannot inline method with stackallocs
                if (inst is CilIntrinsic.Alloca) {
                    return (InlineRejectReason.HasStackAllocs, metrics);
                }
                if (inst is CallInst { Method: MethodDefOrSpec target }) {
                    // Cannot inline self-recursing method
                    if (target.Definition == method.Definition) {
                        return (InlineRejectReason.SelfRecursion, metrics);
                    }
                    metrics.BaseCost += Math.Min(target.ParamSig.Count * 2, 20);
                }
                metrics.BaseCost += 3;
            }

            // Penalize methods with more than one block
            if (block != method.EntryBlock) {
                metrics.BaseCost += 5;
            }

            // Throws suggest cold blocks.
            // This is probably a bad heuristic for methods that are too eager to validate arguments...
            // Would be better to penalize non-returning methods instead. 
            if (block.Last is ThrowInst) {
                metrics.BaseCost += 20;
            } else if (block.Last is ReturnInst) {
                numReturns++;
                metrics.BaseCost -= 3;
            }

            if (metrics.BaseCost >= 3000) { // bail if too long / expansive
                return (InlineRejectReason.Overbudget, metrics);
            }
        }

        if (numReturns == 0) {
            return (InlineRejectReason.DoesNotReturn, metrics);
        }
        metrics.HasLoops = ControlFlowFacts.HasCycle(method);
        return (InlineRejectReason.Accepted, metrics);
    }

    private static int EvaluateConstantBenefit(MethodBody method, ReadOnlySpan<Value> args)
    {
        var constants = new Dictionary<Value, Const>();
        var worklist = new Queue<Instruction>();
        var credited = new HashSet<Instruction>();
        for (int i = 0; i < args.Length; i++) {
            if (args[i] is not Const constant) continue;
            constants.Add(method.Args[i], constant);
            foreach (var user in method.Args[i].Users()) worklist.Enqueue(user);
        }

        int benefit = 0;
        while (worklist.TryDequeue(out var inst)) {
            if (inst is BranchInst { IsConditional: true } branch && ConstFolding.FoldCondition(GetValue(branch.Cond)) != null ||
                inst is SwitchInst table && GetValue(table.TargetIndex) is ConstInt) {
                if (credited.Add(inst)) benefit += 60;
                continue;
            }
            var folded = inst switch {
                BinaryInst b => ConstFolding.FoldBinary(b.Op, GetValue(b.Left), GetValue(b.Right)),
                CompareInst c => ConstFolding.FoldCompare(c.Op, GetValue(c.Left), GetValue(c.Right)),
                UnaryInst u => ConstFolding.FoldUnary(u.Op, GetValue(u.Value)),
                ConvertInst c => ConstFolding.FoldConvert(GetValue(c.Value), c.ResultType, c.CheckOverflow, c.SrcUnsigned),
                SelectInst s => ConstFolding.FoldSelect(GetValue(s.Cond), GetValue(s.IfTrue), GetValue(s.IfFalse)),
                _ => null
            };
            if (folded == null) continue;

            // Credit each eliminated operation once, at its expansion cost.
            // Only constant results can make dependent expressions constant.
            if (credited.Add(inst)) benefit += 3;
            if (folded is Const constant && constants.TryAdd(inst, constant)) {
                foreach (var user in inst.Users()) worklist.Enqueue(user);
            }
        }
        return benefit;

        Value GetValue(Value value) => constants.TryGetValue(value, out var constant) ? constant : value;
    }

    private static void UpdateArgumentUseInfo(ref ArgumentMetrics metrics, TrackedValue value, HashSet<Instruction> visited)
    {
        foreach (var use in value.Uses()) {
            if (use.OperIndex != 0 || !visited.Add(use.Parent)) continue;
            if (use.Parent is CallInst { IsVirtual: true, IsStatic: false, Method: var target } &&
                (target.Attribs & (MethodAttributes.Virtual | MethodAttributes.Final)) == MethodAttributes.Virtual) {
                metrics.BenefitIfKnownType += 40;
            } else if (use.Parent is CilIntrinsic.CastClass or CilIntrinsic.AsInstance) {
                metrics.BenefitIfKnownType += 3;
                UpdateArgumentUseInfo(ref metrics, use.Parent, visited);
            }
        }
    }

    public class FunctionMetrics
    {
        public ArgumentMetrics[] Args = [];
        public int BaseCost;
        public bool HasLoops;
    }
    public struct ArgumentMetrics
    {
        public int BenefitIfInlined;
        public int BenefitIfKnownType;
    }
}

public enum InlineRejectReason
{
    Accepted,
    Ineligible,     // some other arbitrary reason
    UnknownTarget,  // method target is not known statically (e.g. abstract / pinvoke)
    Overbudget,
    SelfRecursion,
    HasStackAllocs,
    DoesNotReturn
}
