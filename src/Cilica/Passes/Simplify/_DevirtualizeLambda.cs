namespace Cilica.Passes;

using System.Runtime.CompilerServices;
using System.Reflection;

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
        if (call is not { Method.Name: "Invoke", IsStatic: false, Args: [var lambdaInstance, ..] }) return false;

        if (lambdaInstance is PhiInst { NumArgs: 2 } phi) {
            var phiArg1 = phi.GetValue(0);
            var phiArg2 = phi.GetValue(1);
            var allocInst = (phiArg1 as NewObjInst) ?? (phiArg2 as NewObjInst);
            var cacheLoad = (phiArg1 as LoadInst) ?? (phiArg2 as LoadInst);

            if (allocInst == null || cacheLoad == null ||
                !MatchCache(phi, allocInst, cacheLoad, out var branch, out var store) ||
                !DevirtWithCtorArgs(call, allocInst, cacheLoad)) return false;

            // Last lambda to be inlined is responsible for cleanup
            if (phi.NumUses == 0 && !RetainsOpenBinding(allocInst)) {
                branch.Cond = ConstInt.CreateI(0);
                phi.Remove();
                store.Remove();
                allocInst.Remove();
            }
            return true;
        } else if (lambdaInstance is NewObjInst immAlloc) {
            if (!DevirtWithCtorArgs(call, immAlloc)) return false;
            
            if (immAlloc.NumUses == 0 && !RetainsOpenBinding(immAlloc)) {
                immAlloc.Remove();
            }
            return true;
        }
        return false;

        // Open binding can have runtime-specific failures and initialization
        // effects. Rewriting Invoke does not remove the original construction.
        static bool RetainsOpenBinding(NewObjInst allocation)
            => allocation.Args is [ConstNull, FuncAddrInst { Method.IsInstance: true }];

        bool DevirtWithCtorArgs(CallInst call, NewObjInst alloc, LoadInst? cacheLoad = null)
        {
            if (alloc is not { Args: [var instanceObj, FuncAddrInst { Method: var method } addr] }) return false;
            if (addr.Constraint != null) return false;
            if (instanceObj is ConstNull) {
                if (addr.IsVirtual) return false;
                CallInst? replacement;
                if (method.IsStatic) {
                    if (call.NumArgs - 1 != method.ParamSig.Count) return false;
                    replacement = new CallInst(method, call.Args[1..].ToArray()) {
                        IsTailCall = call.IsTailCall, ResultIsNonNull = call.ResultIsNonNull, DebugLocation = call.DebugLocation
                    };
                } else {
                    replacement = CreateOpenInstanceDelegateCall(call, method);
                    if (replacement == null) return false;
                }
                logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}'.");
                OnCallReplaced?.Invoke(call, replacement);
                call.ReplaceWith(replacement, insertIfInst: true);
                return true;
            }

            if (method.IsStatic || call.NumArgs != method.ParamSig.Count) return false;
            // A delegate may capture an unchecked or independently resolved
            // virtual target. Only replace it when a direct call has the same receiver.
            if (!method.DeclaringType.IsValueType &&
                !instanceObj.ResultType.IsAssignableTo(method.DeclaringType)) return false;
            if (addr.IsVirtual && addr.Object != instanceObj) return false;

            // Cached instance targets must be available on both phi edges.
            if (cacheLoad != null) {
                if (!IRMatcher.StaticFieldLoad(instanceObj, out var field) ||
                    !field.Attribs.HasFlag(FieldAttributes.InitOnly)) return false;
                var fieldAddr = new FieldAddrInst(field);
                var newLoad = new LoadInst(fieldAddr);
                // Reading the cache already initialized this type. Its readonly
                // receiver can be loaded there, outside any consuming loop.
                // The single-entry allocation arm makes the cache block dominate
                // the merge and every use of the delegate phi.
                if (cacheLoad.Address is FieldAddrInst cacheAddr && cacheAddr.Field.DeclaringType == field.DeclaringType &&
                    alloc.Block.NumPreds == 1 && alloc.Block.Preds.First() == cacheLoad.Block) {
                    fieldAddr.InsertAfter(cacheLoad);
                    newLoad.InsertAfter(fieldAddr);
                } else {
                    fieldAddr.InsertBefore(call);
                    newLoad.InsertBefore(call);
                }
                instanceObj = newLoad;
            }
            if (method.DeclaringType.IsValueType) {
                var address = new CilIntrinsic.UnboxRef(method.DeclaringType, instanceObj);
                address.InsertBefore(call);
                instanceObj = address;
            }
            logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}' using its captured receiver.");
            call.Method = method;
            call.SetArg(0, instanceObj);
            call.IsVirtual = addr.IsVirtual;
            return true;
        }
        static bool MatchCache(PhiInst phi, NewObjInst allocInst, LoadInst cacheLoad,
            out BranchInst branch, out StoreInst store)
        {
            branch = null!;
            store = null!;
            var condBlock = cacheLoad.Block;

            if (!(
                // First load block's must end with "goto cache == null ? BB_CacheLoad : BB_Result"
                condBlock.Last is
                    BranchInst { Cond: CompareInst { Op: CompareOp.Ne, Left: var condCache, Right: ConstNull } } br &&
                    br.Then == phi.Block &&
                    br.Else == allocInst.Block &&
                    condCache == cacheLoad &&
                // BB_CacheLoad must store to the cache field
                allocInst.NumUses == 2 && // phi and next store
                ((phi.GetBlock(0) == condBlock && phi.GetBlock(1) == allocInst.Block) ||
                 (phi.GetBlock(1) == condBlock && phi.GetBlock(0) == allocInst.Block)) &&
                phi.GetValue(condBlock) == cacheLoad && phi.GetValue(allocInst.Block) == allocInst &&
                IRMatcher.StaticFieldLoad(cacheLoad, out var cacheField) &&
                allocInst.Next?.Next is StoreInst cacheStore && 
                cacheStore.Value == allocInst &&
                (cacheStore.Address as FieldAddrInst)?.Field == cacheField &&
                cacheField.DeclaringType is TypeDefOrSpec declType && 
                declType.Definition.HasCustomAttrib(typeof(CompilerGeneratedAttribute))
            )) return false;

            branch = br;
            store = cacheStore;
            return true;
        }
    }

    internal static CallInst? CreateOpenInstanceDelegateCall(CallInst call, MethodDesc method)
    {
        if (call.IsReadOnly || call.Constraint != null || call.Method.IsGeneric ||
            (call.Method.ImplAttribs & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Runtime ||
            call.NumArgs - 1 != method.ParamSig.Count || call.Method.ReturnSig != method.ReturnSig ||
            !call.Method.ParamSig.Skip(1).SequenceEqual(method.ParamSig)) return null;

        // Open virtual delegates dispatch on the supplied receiver. Nonvirtual
        // delegates may pass null through to a method that never dereferences it.
        bool dispatch = !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        return new CallInst(method, call.Args[1..].ToArray(), isVirtual: dispatch) {
            IsTailCall = call.IsTailCall,
            ResultIsNonNull = call.ResultIsNonNull,
            DebugLocation = call.DebugLocation
        };
    }
}
