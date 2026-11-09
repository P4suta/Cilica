namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class FoldReflection
{
    private static bool FoldDelegate(CallInst call, DelegateCreation creation, MethodDefOrSpec method, InliningAccess access)
    {
        var delegateType = creation.Type;
        if (!CanTakeMethodAddress(method, access) ||
            method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") ||
            delegateType.IsUnboundGeneric || delegateType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) != true) return false;

        var invoke = delegateType.FindMethod("Invoke", throwIfNotFound: false);
        var constructor = delegateType.FindMethod(".ctor", new MethodSig(PrimType.Void, [PrimType.Object, PrimType.IntPtr], true), throwIfNotFound: false);
        if (invoke == null || constructor == null || !access.CanUseMember(constructor) ||
            invoke.ReturnSig != method.ReturnSig || invoke.CallConv != CallConvention.Managed) return false;

        int count = invoke.ParamSig.Count - 1;
        bool open = count == method.ParamSig.Count;
        int offset;
        if (open) {
            if (creation.Target is not ConstNull) return false;
            offset = 0;
        } else {
            if (!creation.AllowClosed || count != method.ParamSig.Count - 1) return false;
            if (method.IsInstance) {
                // Reflection can bind a closed delegate to null, whereas the IL
                // delegate constructor requires a non-null instance target.
                if (!CanUseReceiver(creation.Target, method.DeclaringType)) return false;
            } else if (!method.ParamSig[0].Type.IsManagedObject() || !CanConvert(creation.Target, method.ParamSig[0].Type)) {
                return false;
            }
            offset = 1;
        }
        for (int i = 0; i < count; i++) {
            if (invoke.ParamSig[i + 1] != method.ParamSig[i + offset]) return false;
        }

        var builder = new IRBuilder(call, InsertionDir.Before).SetNextDebugLocation(call.DebugLocation);
        // Reflection honors precise type initialization even when the runtime's
        // specialized delegate constructor does not run the static constructor.
        if (delegateType.RequiresTypeInitialization()) builder.CreateTypeInitialization(delegateType);
        bool virtualTarget = !open && method.IsInstance && !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        var address = builder.Emit(new FuncAddrInst(method, virtualTarget ? creation.Target : null));
        call.ReplaceWith(builder.CreateNewObj(constructor, creation.Target, address));
        return true;
    }

    private sealed record DelegateCreation(TypeDefOrSpec Type, Value MethodInfo, Value Target, bool AllowClosed, Value? TypeValue)
    {
        public static DelegateCreation? Match(CallInst call, ModuleResolver resolver)
        {
            if (call.Method.Name != "CreateDelegate") return null;
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            TypeDesc? delegateType;
            Value methodInfo, target = ConstNull.Create();
            Value? typeValue = null;
            bool allowClosed;

            if (call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) && !call.IsStatic) {
                methodInfo = call.Args[0];
                if (call.Method.GenericParams is [var argument] &&
                    new MethodSig(argument, [], true, numGenPars: 1).Matches(call.Method)) {
                    delegateType = argument;
                    allowClosed = false;
                } else if (call.Method.GenericParams is [var boundArgument] &&
                    new MethodSig(boundArgument, [PrimType.Object], true, numGenPars: 1).Matches(call.Method)) {
                    delegateType = boundArgument;
                    target = call.Args[1];
                    allowClosed = true;
                } else if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType)) {
                    delegateType = TypeUtils.GetReflectedType(typeValue = call.Args[1]);
                    allowClosed = false;
                } else if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType, PrimType.Object)) {
                    delegateType = TypeUtils.GetReflectedType(typeValue = call.Args[1]);
                    target = call.Args[2];
                    allowClosed = true;
                } else return null;
            } else if (call.Method.DeclaringType.IsCorelibType(typeof(Delegate)) && call.IsStatic) {
                var methodInfoType = resolver.Import(typeof(MethodInfo));
                if (new MethodSig(delegateBase, [typeType, methodInfoType], false).Matches(call.Method) ||
                    new MethodSig(delegateBase, [typeType, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                    methodInfo = call.Args[1];
                    allowClosed = false;
                } else if (new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType], false).Matches(call.Method) ||
                    new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                    methodInfo = call.Args[2];
                    target = call.Args[1];
                    allowClosed = true;
                } else return null;
                delegateType = TypeUtils.GetReflectedType(typeValue = call.Args[0]);
            } else return null;

            return delegateType is TypeDefOrSpec nominal ? new(nominal, methodInfo, target, allowClosed, typeValue) : null;
        }
    }
}
