namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class FoldReflection
{
    private static bool CanTakeMethodAddress(MethodDefOrSpec method, InliningAccess access)
        => !method.IsGeneric && !method.DeclaringType.IsUnboundGeneric &&
            method.CallConv == CallConvention.Managed && !method.IsRuntimeAsync &&
            !method.Attribs.HasFlag(MethodAttributes.Abstract) &&
            !method.DeclaringType.IsCorelibType(typeof(Nullable<>)) &&
            IsBoxable(method.DeclaringType) && !Compilation.IsFrameSensitive(method) &&
            !TypeUtils.HasClassMethodImpls(method) && access.CanUseMember(method);

    private static bool IsMethodHandleCall(CallInst call)
        => call.Method.ReturnType.IsCorelibType(typeof(RuntimeMethodHandle)) &&
            Matches(call, typeof(MethodBase), "get_MethodHandle", call.Method.ReturnType);

    private static bool IsFunctionPointerCall(CallInst call)
        => Matches(call, typeof(RuntimeMethodHandle), "GetFunctionPointer", PrimType.IntPtr);

    private static bool FoldMethodHandle(MethodTransformContext ctx, CallInst call, MethodDefOrSpec method, InliningAccess access)
    {
        // Instance handles can identify unboxing or virtual entry points whose
        // receiver convention differs from an ordinary ldftn.
        if (!method.IsStatic || !CanTakeMethodAddress(method, access)) return false;
        call.ReplaceWith(new CilIntrinsic.LoadHandle(ctx.Module.Resolver, method) { DebugLocation = call.DebugLocation }, insertIfInst: true);
        return true;
    }

    private static bool FoldFunctionPointer(CallInst call, MethodDefOrSpec method, InliningAccess access)
    {
        if (!method.IsStatic || !CanTakeMethodAddress(method, access)) return false;
        var builder = new IRBuilder(call, InsertionDir.Before).SetNextDebugLocation(call.DebugLocation);
        call.ReplaceWith(builder.CreateConvert(builder.Emit(new FuncAddrInst(method)), PrimType.IntPtr));
        return true;
    }

    private static MethodDefOrSpec? GetFunctionPointerTarget(CallInst call)
    {
        if (!IsFunctionPointerCall(call) || call.Args[0] is not LocalSlot { IsPinned: false, IsHardExposed: false } slot) return null;
        StoreInst? definition = null;
        foreach (var (user, operand) in slot.Uses()) {
            if (operand != 0) return null;
            if (user is StoreInst { Flags: PointerFlags.None } store && store.ElemType == slot.Type) {
                if (definition != null) return null;
                definition = store;
            } else if (user is LoadInst { Flags: PointerFlags.None } load && load.ElemType == slot.Type) {
                continue;
            } else if (user is not CallInst use || !IsFunctionPointerCall(use)) {
                return null;
            }
        }
        if (definition?.Block != call.Block || definition.Value is not CilIntrinsic.LoadHandle { Entity: MethodDefOrSpec target }) return null;
        // Local addresses passed to other operations may be mutated. With one
        // store and only reads, this value is fixed after the store completes.
        for (var previous = call.Prev; previous != null; previous = previous.Prev) {
            if (previous == definition) return target;
        }
        return null;
    }
}
