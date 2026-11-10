namespace Cilica.Passes;

using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.IR.Utils;

partial class SimplifyInsts
{
    // Directize delegate invokes if target is known:
    //
    // Func = Func`2[int, bool]
    //  BB_Header:
    //    r2 = ldfld Data::LambdaCache1 -> Func
    //    r3 = cmp.ne r2, null -> bool
    //    goto r3 ? BB_Result : BB_CacheLoad
    //  BB_CacheLoad:
    //    r5 = ldfld Data::Instance -> Data
    //    r6 = funcaddr bool Data::Lambda1(Data, int) -> void*
    //    r7 = newobj Func::.ctor(object: r5, nint: r6) -> Func
    //    stfld Data::LambdaCache1, r7
    //    goto BB_Result
    //  BB_Result:
    //    r9 = phi [BB_Header: r2], [BB_CacheLoad: r7] -> Func
    //    ...
    //    r25 = callvirt Func::Invoke(this: r9, int: r24) -> bool
    // ->
    //  BB_Result:
    //    r5 = ldfld Data::Instance -> Data
    //    r25 = call Data::Lambda1(Data: r5, int: r24) -> bool
    private bool DevirtualizeLambda(CallInst call, ICompilationLogger logger)
    {
        if (call is not { Method.Name: "Invoke", IsStatic: false, Args: [var instance, ..] }) return false;

        DelegateCache? cache = instance is PhiInst phi ? DelegateCache.Match(phi) : null;
        var allocation = cache?.Allocation ?? instance as NewObjInst;
        if (allocation == null || !DevirtualizeDelegate(call, allocation, cache?.Load, logger)) return false;

        // Open binding can have runtime-specific failures and initialization
        // effects. Rewriting Invoke does not remove the original construction.
        if (allocation.Args is [ConstNull, FuncAddrInst { Method.IsInstance: true }]) return true;
        if (cache != null) cache.RemoveIfUnused();
        else if (allocation.NumUses == 0) allocation.Remove();
        return true;
    }

    private bool DevirtualizeDelegate(CallInst call, NewObjInst allocation, LoadInst? cacheLoad, ICompilationLogger logger)
    {
        if (allocation is not { Args: [var target, FuncAddrInst address] }) return false;
        if (address.Constraint != null) return false;
        var method = address.Method;

        if (target is ConstNull) {
            if (address.IsVirtual) return false;
            var replacement = CreateOpenDelegateCall(call, method);
            if (replacement == null) return false;

            logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}'.");
            OnCallReplaced?.Invoke(call, replacement);
            call.ReplaceWith(replacement, insertIfInst: true);
            return true;
        }

        if (method.IsStatic || call.NumArgs != method.ParamSig.Count) return false;
        // A delegate may capture an unchecked or independently resolved
        // virtual target. Only replace it when a direct call has the same receiver.
        if (!method.DeclaringType.IsValueType && !target.ResultType.IsAssignableTo(method.DeclaringType)) return false;
        if (address.IsVirtual && address.Object != target) return false;

        if (cacheLoad != null) {
            // A cached target must be available on both incoming edges.
            if (!IRMatcher.StaticFieldLoad(target, out var field) || !field.Attribs.HasFlag(FieldAttributes.InitOnly)) return false;
            target = LoadCachedReceiver(field, cacheLoad, allocation, call);
        }
        if (method.DeclaringType.IsValueType) {
            var unboxed = new CilIntrinsic.UnboxRef(method.DeclaringType, target);
            unboxed.InsertBefore(call);
            target = unboxed;
        }

        logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}' using its captured receiver.");
        call.Method = method;
        call.SetArg(0, target);
        call.IsVirtual = address.IsVirtual;
        return true;
    }

    private static CallInst? CreateOpenDelegateCall(CallInst call, MethodDesc method)
    {
        if (method.IsInstance) return DelegateUtils.CreateOpenInstanceCall(call, method);
        if (call.NumArgs - 1 != method.ParamSig.Count) return null;
        return new CallInst(method, call.Args[1..].ToArray()) {
            IsTailCall = call.IsTailCall,
            ResultIsNonNull = call.ResultIsNonNull,
            DebugLocation = call.DebugLocation
        };
    }

    private static Value LoadCachedReceiver(FieldDesc field, LoadInst cacheLoad, NewObjInst allocation, CallInst call)
    {
        var address = new FieldAddrInst(field);
        var load = new LoadInst(address);
        // Reading the cache already initialized this type. Its readonly receiver
        // can be loaded there, outside any consuming loop, when the cache block
        // dominates the allocation arm and the merge.
        bool loadAtCache = cacheLoad.Address is FieldAddrInst cacheAddress &&
            cacheAddress.Field.DeclaringType == field.DeclaringType &&
            allocation.Block.NumPreds == 1 && allocation.Block.Preds.First() == cacheLoad.Block;
        if (loadAtCache) {
            address.InsertAfter(cacheLoad);
            load.InsertAfter(address);
        } else {
            address.InsertBefore(call);
            load.InsertBefore(call);
        }
        return load;
    }

    // A compiler-generated cache merges a previously stored delegate with the
    // same constructor on the cache-miss edge. Ordinary mutable fields do not
    // establish this binding.
    private sealed record DelegateCache(PhiInst Merge, NewObjInst Allocation, LoadInst Load, BranchInst Branch, StoreInst Store)
    {
        public static DelegateCache? Match(PhiInst phi)
        {
            if (phi.NumArgs != 2) return null;
            var first = phi.GetValue(0);
            var second = phi.GetValue(1);
            var allocation = (first as NewObjInst) ?? (second as NewObjInst);
            var load = (first as LoadInst) ?? (second as LoadInst);
            if (allocation == null || load == null) return null;

            // First load block's must end with "goto cache == null ? BB_CacheLoad : BB_Result"
            var header = load.Block;
            if (header.Last is not BranchInst {
                Cond: CompareInst { Op: CompareOp.Ne, Left: var cached, Right: ConstNull }
            } branch) return null;
            if (cached != load || branch.Then != phi.Block || branch.Else != allocation.Block) return null;

            bool matchingEdges =
                (phi.GetBlock(0) == header && phi.GetBlock(1) == allocation.Block) ||
                (phi.GetBlock(1) == header && phi.GetBlock(0) == allocation.Block);
            if (!matchingEdges) return null;
            if (phi.GetValue(header) != load || phi.GetValue(allocation.Block) != allocation) return null;

            // BB_CacheLoad must store to the cache field
            if (allocation.NumUses != 2) return null; // phi and next store
            if (!IRMatcher.StaticFieldLoad(load, out var field)) return null;
            if (allocation.Next?.Next is not StoreInst store || store.Value != allocation) return null;
            if ((store.Address as FieldAddrInst)?.Field != field) return null;
            if (field.DeclaringType is not TypeDefOrSpec owner ||
                !owner.Definition.HasCustomAttrib(typeof(CompilerGeneratedAttribute))) return null;

            return new(phi, allocation, load, branch, store);
        }

        public void RemoveIfUnused()
        {
            // Last lambda to be inlined is responsible for cleanup
            if (Merge.NumUses != 0) return;
            Branch.Cond = ConstInt.CreateI(0);
            Merge.Remove();
            Store.Remove();
            Allocation.Remove();
        }
    }
}
