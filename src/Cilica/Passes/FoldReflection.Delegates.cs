namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class FoldReflection
{
    private static bool FoldDelegate(CallInst call, DelegateCreation creation, MethodDefOrSpec method, InliningAccess access, ReflectionTypeFacts types)
    {
        var delegateType = creation.Type;
        if (!CanTakeMethodAddress(method, access, types, allowAbstract: true) ||
            method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") ||
            !ReflectionTypeFacts.IsRuntimeType(delegateType) || delegateType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) != true) return false;

        var invoke = delegateType.FindMethod("Invoke", throwIfNotFound: false);
        var constructor = delegateType.FindMethod(".ctor", new MethodSig(PrimType.Void, [PrimType.Object, PrimType.IntPtr], true), throwIfNotFound: false);
        if (invoke == null || constructor == null || !access.CanUseMember(constructor) ||
            invoke.ReturnSig != method.ReturnSig || invoke.CallConv != CallConvention.Managed) return false;

        int count = invoke.ParamSig.Count - 1;
        bool open = count == method.ParamSig.Count;
        int offset;
        if (open) {
            // ldftn cannot name an abstract method. A closed virtual delegate
            // can instead obtain its callable entry point through ldvirtftn.
            if (creation.Target is not ConstNull || method.Attribs.HasFlag(MethodAttributes.Abstract)) return false;
            // The runtime's open-virtual dispatch stub rejects generic methods
            // on reference types. Keep that failure at the reflection call site.
            if (method.IsGeneric && method.IsInstance && !method.DeclaringType.IsValueType &&
                method.Attribs.HasFlag(MethodAttributes.Virtual)) return false;
            offset = 0;
        } else {
            if (!creation.AllowClosed || count != method.ParamSig.Count - 1) return false;
            if (method.IsInstance) {
                // Reflection can bind a closed delegate to null, whereas the IL
                // delegate constructor requires a non-null instance target.
                if (!CanUseReceiver(creation.Target, method.DeclaringType, types)) return false;
            } else if (!types.IsReferenceType(method.ParamSig[0].Type) || !CanConvert(creation.Target, method.ParamSig[0].Type, types)) {
                return false;
            }
            offset = 1;
        }
        for (int i = 0; i < count; i++) {
            if (invoke.ParamSig[i + 1] != method.ParamSig[i + offset]) return false;
        }

        bool virtualTarget = !open && method.IsInstance && !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        if (virtualTarget && !call.Uses().All(use => use.OperIndex == 0 && (use.Parent is CilIntrinsic.NullCheck ||
                use.Parent is CallInst invocation && invocation.Method.Name == "Invoke" && invocation.Method.DeclaringType == delegateType))) {
            var receiverType = Devirtualize.ExactType(creation.Target);
            if (receiverType is PrimType primitive) receiverType = primitive.GetDefinition(types.Resolver);
            // Reflection retains the requested virtual method as delegate identity.
            // A constructor using ldvirtftn can expose the selected override instead.
            if (receiverType is not TypeDefOrSpec exactType ||
                method.DeclaringType.IsInterface && TypeUtils.CanHaveDynamicInterfaces(exactType) ||
                !VirtualDispatch.SameMethod(VirtualDispatch.Resolve(method, exactType), method)) return false;
        }

        var builder = new IRBuilder(call, InsertionDir.Before).SetNextDebugLocation(call.DebugLocation);
        // Reflection honors precise type initialization even when the runtime's
        // specialized delegate constructor does not run the static constructor.
        if (delegateType.RequiresTypeInitialization()) builder.CreateTypeInitialization(delegateType);
        // Keep the boxed receiver for virtual addresses; the runtime may need
        // both an unboxing stub and the constructed method's instantiation context.
        var address = builder.Emit(new FuncAddrInst(method, virtualTarget ? creation.Target : null));
        call.ReplaceWith(builder.CreateNewObj(constructor, creation.Target, address));
        return true;
    }

    private sealed record DelegateCreation(TypeDefOrSpec Type, Value MethodInfo, Value Target, bool AllowClosed)
    {
        public static DelegateCreation? Match(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            if (call.Method.Name != "CreateDelegate" || !call.IsVirtual && call.Method.Attribs.HasFlag(MethodAttributes.Virtual)) return null;
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            TypeDesc? delegateType;
            Value methodInfo, target = ConstNull.Create();
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
                    delegateType = lookups.ResolveType(call.Args[1]);
                    allowClosed = false;
                } else if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType, PrimType.Object)) {
                    delegateType = lookups.ResolveType(call.Args[1]);
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
                delegateType = lookups.ResolveType(call.Args[0]);
            } else return null;

            return delegateType is TypeDefOrSpec nominal ? new(nominal, methodInfo, target, allowClosed) : null;
        }
    }
}
