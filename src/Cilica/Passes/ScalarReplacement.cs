namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.IR.Utils;

/// <summary> Inlines fields of non-escaping object allocations into local variables. </summary>
public class ScalarReplacement : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var allocs = new List<NewObjInst>();
        
        // Find non-escaping object allocations
        foreach (var inst in ctx.Method.Instructions()) {
            if (inst is NewObjInst alloc && IsSimpleCtor(ctx, alloc) && !Escapes(alloc)) {
                allocs.Add(alloc);
            }
        }

        // Replace fields with local variables
        foreach (var obj in allocs) {
            InlineAlloc(obj, ctx.Compilation);
        }

        return allocs.Count > 0 ? MethodInvalidations.ControlFlow : 0; // ctors are inlined and may add new blocks.
    }

    private static void InlineAlloc(NewObjInst alloc, Compilation comp)
    {
        // FieldSpecs don't have proper equality comparisons and may have multiple 
        // instances for the same spec, we must use the definition as key instead.
        var fieldSlots = new Dictionary<FieldDef, LocalSlot>();

        // Zero-init fields (this is necessary for correct SSA promotion)
        var builder = new IRBuilder(alloc, InsertionDir.Before);

        for (var type = alloc.ResultType; type != null; type = type.BaseType) {
            foreach (var field in type.Fields) {
                if (field.IsInstance) builder.CreateInitObj(GetSlot(field));
            }
        }

        // At this point we know that the constructor doesn't let the instance escape.
        // Inlining it here will add the accessed fields to the use chain of `alloc`.
        InlineMethods.Inline(alloc, (MethodDefOrSpec)alloc.Constructor, [alloc, ..alloc.Args], comp);

        foreach (var user in alloc.Users()) {
            if (user is FieldAddrInst addr) {
                addr.ReplaceWith(GetSlot(addr.Field));
            } else {
                Debug.Assert(IsObjectCtorCall(user));
                user.Remove();
            }
        }

        // Remove redundant allocation
        alloc.Remove();

        LocalSlot GetSlot(FieldDesc field)
        {
            var def = ((FieldDefOrSpec)field).Definition;
            return fieldSlots.GetOrAddRef(def) ??= builder.Method.CreateVar(field.Type, "sroa." + field.Name);
        }
    }

    private static bool IsSimpleCtor(MethodTransformContext ctx, NewObjInst alloc)
    {
        if (alloc.Constructor is not MethodDefOrSpec { Definition: var ctor }) {
            return false;
        }
        if (alloc.ResultType.IsValueType || HasFinalizer(alloc.ResultType)) {
            return false;
        }

        var advisor = ctx.Compilation.GetAnalysis<InliningAdvisor>();

        if (advisor.EarlyCheck(ctor) != InlineRejectReason.Accepted) {
            return false;
        }
        if (ctor.Body == null && !advisor.ImportBodyForInlining(ctor)) {
            return false;
        }

        // Ctor must be small and instance obj cannot escape
        var body = ctor.Body!;
        return advisor.EvaluateInliningCost(body, [alloc, ..alloc.Args]) < 100 && !Escapes(body.Args[0], isCtor: true) &&
               ctx.Compilation.CanInlineBody(ctx.Definition, (MethodDefOrSpec)alloc.Constructor, scalarReplacement: true);
    }
    private static bool Escapes(TrackedValue obj, bool isCtor = false)
    {
        return !obj.Users().All(u =>
            u is FieldAddrInst addr && !AddressEscapes(addr) || (isCtor && IsObjectCtorCall(u)));
    }

    private static bool AddressEscapes(TrackedValue address)
        => address.Uses().Any(use => use.Parent switch {
            LoadInst => use.OperIndex != 0,
            StoreInst => use.OperIndex != 0,
            FieldAddrInst nested => use.OperIndex != 0 || AddressEscapes(nested),
            CilIntrinsic.MemSet => use.OperIndex != 0,
            CilIntrinsic.MemCopy => use.OperIndex > 1,
            _ => true
        });

    private static bool HasFinalizer(TypeDesc type)
    {
        for (TypeDesc? curr = type; curr != null && curr != PrimType.Object && !curr.IsCorelibType(typeof(object)); curr = curr.BaseType) {
            if (curr.Methods.Any(m => m.Name == "Finalize" && m.IsInstance && m.ParamSig.Count == 1 &&
                m.Attribs.HasFlag(System.Reflection.MethodAttributes.Virtual))) return true;
        }
        return false;
    }

    private static bool IsObjectCtorCall(Instruction inst)
    {
        return inst is CallInst { Method.Name: ".ctor", Method.DeclaringType: var declType } &&
            declType.IsCorelibType(typeof(object));
    }
}
