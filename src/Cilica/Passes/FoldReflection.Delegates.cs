namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    private readonly record struct DelegateBinding(MethodDesc Constructor, bool IsOpen);

    private bool FoldDelegate(CallInst call, DelegateCreation creation, MethodDefOrSpec method)
    {
        if (BindDelegate(creation, method) is not { } binding) return false;
        if (binding.IsOpen && method.IsInstance && MustKeepOpenInstanceFactory(method)) {
            // Binding failures and initialization still belong to the factory.
            // Its successful invocations can use the proven target directly.
            return FoldOpenInstanceInvocations(call, creation.Type, method);
        }

        bool virtualTarget = !binding.IsOpen && method.IsInstance &&
            !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        if (virtualTarget && !PreservesDelegateIdentity(call, creation, method)) return false;

        var builder = BuilderBefore(call);
        // Reflection honors precise type initialization even when the runtime's
        // specialized delegate constructor does not run the static constructor.
        if (creation.Type.RequiresTypeInitialization()) builder.CreateTypeInitialization(creation.Type);
        // Keep the boxed receiver for virtual addresses; the runtime may need
        // both an unboxing stub and the constructed method's instantiation context.
        var address = builder.Emit(new FuncAddrInst(method, virtualTarget ? creation.Target : null));
        call.ReplaceWith(builder.CreateNewObj(binding.Constructor, creation.Target, address));
        return true;
    }

    private DelegateBinding? BindDelegate(DelegateCreation creation, MethodDefOrSpec method)
    {
        if (!CanTakeMethodAddress(method, allowAbstract: true)) return null;
        if (method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute")) return null;

        var delegateType = creation.Type;
        if (!ReflectionTypeFacts.IsRuntimeType(delegateType)) return null;
        if (delegateType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) != true) return null;
        var invoke = delegateType.FindMethod("Invoke", throwIfNotFound: false);
        var constructor = delegateType.FindMethod(".ctor",
            new MethodSig(PrimType.Void, [PrimType.Object, PrimType.IntPtr], true), throwIfNotFound: false);
        if (invoke == null || constructor == null || !_access.CanUseMember(constructor)) return null;
        if (invoke.ReturnSig != method.ReturnSig || invoke.CallConv != CallConvention.Managed) return null;

        // An open delegate supplies every method argument, including 'this'.
        // A closed delegate captures the method's first argument in its target.
        int count = invoke.ParamSig.Count - 1;
        bool open = count == method.ParamSig.Count;
        if (open) {
            if (creation.Target is not ConstNull) return null;
        } else {
            if (!creation.AllowClosed || count != method.ParamSig.Count - 1) return null;
            if (!CanBindClosedTarget(creation.Target, method)) return null;
        }

        int offset = open ? 0 : 1;
        if (!invoke.ParamSig.Skip(1).SequenceEqual(method.ParamSig.Skip(offset))) return null;
        return new(constructor, open);
    }

    private bool CanBindClosedTarget(Value target, MethodDefOrSpec method)
    {
        if (method.IsInstance) {
            // Reflection can bind a closed delegate to null, whereas the IL
            // delegate constructor requires a non-null instance target.
            return CanUseReceiver(target, method.DeclaringType);
        }
        var firstParameter = method.ParamSig[0].Type;
        return _types.IsReferenceType(firstParameter) && CanConvert(target, firstParameter);
    }

    private bool MustKeepOpenInstanceFactory(MethodDefOrSpec method)
    {
        // NativeAOT cannot express an open instance binding with constructor IL.
        if (_ctx.Compilation.Settings.NativeAotCompatibility) return true;
        // Abstract and generic virtual bindings need runtime dispatch machinery;
        // a method address alone does not preserve their binding failures.
        if (method.Attribs.HasFlag(MethodAttributes.Abstract)) return true;
        return method.IsGeneric && !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
    }

    private bool PreservesDelegateIdentity(CallInst factory, DelegateCreation creation, MethodDefOrSpec method)
    {
        // Invoke and null checks cannot observe which MethodInfo the delegate exposes.
        if (factory.Uses().All(use => use.OperIndex == 0 &&
            (use.Parent is CilIntrinsic.NullCheck || MatchDelegateInvocation(use.Parent, creation.Type) != null))) return true;

        var receiverType = Devirtualize.ExactType(creation.Target);
        if (receiverType is PrimType primitive) receiverType = primitive.GetDefinition(_types.Resolver);
        if (receiverType is not TypeDefOrSpec exactType) return false;
        if (method.DeclaringType.IsInterface && TypeUtils.CanHaveDynamicInterfaces(exactType)) return false;

        // Reflection retains the requested virtual method as delegate identity.
        // A constructor using ldvirtftn can expose the selected override instead.
        return VirtualDispatch.SameMethod(_lookups.ResolveVirtualMethod(method, exactType), method);
    }

    private bool FoldOpenInstanceInvocations(CallInst factory, TypeDefOrSpec delegateType, MethodDefOrSpec method)
    {
        bool changed = false;
        var pending = new Stack<Instruction>();
        var visited = new HashSet<Instruction>();
        pending.Push(factory);
        while (pending.TryPop(out var value)) {
            if (!visited.Add(value)) continue;
            foreach (var (user, operand) in value.Uses().AsEnumerable().ToArray()) {
                if (operand != 0) continue;
                if (user is CilIntrinsic.CastClass or CilIntrinsic.RefCast or CilIntrinsic.AsInstance &&
                    user.ResultType == delegateType) {
                    pending.Push(user);
                    continue;
                }
                if (MatchDelegateInvocation(user, delegateType) is not { } invocation) continue;
                var direct = DelegateUtils.CreateOpenInstanceCall(invocation, method);
                if (direct == null) continue;

                // A factory that returns null must still fail at invocation,
                // after the invocation arguments have been evaluated.
                if (!invocation.InBounds) BuilderBefore(invocation).CreateNullCheck(value);
                _ctx.Logger.Trace($"Replaced open delegate invocation '{invocation}' in '{invocation.Block}' with a call to '{method}'.");
                invocation.ReplaceWith(direct, insertIfInst: true);
                changed = true;
            }
        }
        return changed;
    }

    private static CallInst? MatchDelegateInvocation(Instruction instruction, TypeDefOrSpec delegateType)
        => instruction is CallInst { Method.Name: "Invoke", IsStatic: false } invocation &&
            invocation.Method.DeclaringType == delegateType ? invocation : null;

    private sealed record DelegateCreation(TypeDefOrSpec Type, Value MethodInfo, Value Target, bool AllowClosed)
    {
        public static DelegateCreation? Match(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            if (call.Method.Name != "CreateDelegate") return null;
            if (!call.IsVirtual && call.Method.Attribs.HasFlag(MethodAttributes.Virtual)) return null;
            if (call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) && !call.IsStatic) {
                return MatchMethodInfoFactory(call, resolver, lookups);
            }
            if (call.Method.DeclaringType.IsCorelibType(typeof(Delegate)) && call.IsStatic) {
                return MatchStaticFactory(call, resolver, lookups);
            }
            return null;
        }

        private static DelegateCreation? MatchMethodInfoFactory(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            TypeDesc? delegateType;
            bool allowClosed;
            if (call.Method.GenericParams is [var argument]) {
                delegateType = argument;
                if (new MethodSig(argument, [], true, numGenPars: 1).Matches(call.Method)) {
                    allowClosed = false;
                } else if (new MethodSig(argument, [PrimType.Object], true, numGenPars: 1).Matches(call.Method)) {
                    allowClosed = true;
                } else return null;
            } else {
                if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType)) {
                    allowClosed = false;
                } else if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType, PrimType.Object)) {
                    allowClosed = true;
                } else return null;
                delegateType = lookups.ResolveType(call.Args[1]);
            }
            var target = allowClosed ? call.Args[^1] : ConstNull.Create();
            return delegateType is TypeDefOrSpec nominal ? new(nominal, call.Args[0], target, allowClosed) : null;
        }

        private static DelegateCreation? MatchStaticFactory(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            var methodInfoType = resolver.Import(typeof(MethodInfo));
            Value methodInfo, target;
            bool allowClosed;
            if (new MethodSig(delegateBase, [typeType, methodInfoType], false).Matches(call.Method) ||
                new MethodSig(delegateBase, [typeType, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                methodInfo = call.Args[1];
                target = ConstNull.Create();
                allowClosed = false;
            } else if (new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType], false).Matches(call.Method) ||
                new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                methodInfo = call.Args[2];
                target = call.Args[1];
                allowClosed = true;
            } else return null;

            return lookups.ResolveType(call.Args[0]) is TypeDefOrSpec delegateType
                ? new(delegateType, methodInfo, target, allowClosed) : null;
        }
    }
}
