namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    private readonly record struct InvocationArguments(Value Array, bool WrapExceptions);

    private InvocationArguments? MatchInvocation(CallInst call)
    {
        if (Matches(call, typeof(MethodBase), "Invoke", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray())) {
            return new(call.Args[2], WrapExceptions: true);
        }

        var resolver = _ctx.Module.Resolver;
        if (!Matches(call, typeof(MethodBase), "Invoke", PrimType.Object, PrimType.Object,
                resolver.Import(typeof(BindingFlags)), resolver.Import(typeof(Binder)),
                PrimType.Object.CreateArray(), resolver.Import(typeof(System.Globalization.CultureInfo)))) return null;

        // A custom binder or culture can change argument conversion. Only the
        // exception-wrapping flag is compatible with the conversions below.
        if (call.Args[3] is not ConstNull || call.Args[5] is not ConstNull) return null;
        if (call.Args[2] is not ConstInt bits) return null;
        var flags = (BindingFlags)bits.Value;
        if ((flags & ~BindingFlags.DoNotWrapExceptions) != 0) return null;
        return new(call.Args[4], WrapExceptions: !flags.HasFlag(BindingFlags.DoNotWrapExceptions));
    }

    private bool FoldMethod(CallInst call, MethodDefOrSpec method)
    {
        if (MatchInvocation(call) is not { } invocation) return false;
        int count = method.ParamSig.Count - (method.IsInstance ? 1 : 0);
        if (!KnownArray.TryRead(invocation.Array, call, count, out var array)) return false;
        if (!FoldInvocation(call, method, call.Args[1], array.Values, invocation.WrapExceptions)) return false;
        array.Remove();
        return true;
    }

    private bool FoldProperty(CallInst call, PropertyDef property, TypeDefOrSpec reflectedType)
    {
        bool load = Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        bool store = Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        if ((!load && !store) || property.Sig.ParamTypes.Count != 0) return false;

        var accessor = (load ? property.Getter : property.Setter)?.GetSpec(new GenericContext(reflectedType));
        if (accessor == null) return false;

        KnownArray? index = null;
        bool hasIndexArgument = call.NumArgs == (load ? 3 : 4);
        if (hasIndexArgument && !KnownArray.TryRead(call.Args[^1], call, 0, out index)) return false;
        if (!FoldInvocation(call, accessor, call.Args[1], load ? [] : [call.Args[2]], wrapExceptions: true)) return false;
        index?.Remove();
        return true;
    }

    private bool FoldInvocation(CallInst reflection, MethodDefOrSpec method, Value receiver, Value[] values, bool wrapExceptions)
    {
        if (!CanReferenceMethod(method) || !CanInvokeSignature(method)) return false;
        if (method.IsInstance && !CanUseReceiver(receiver, method.DeclaringType)) return false;
        if (wrapExceptions && !CanWrapInvocation(reflection)) return false;

        int offset = method.IsInstance ? 1 : 0;
        if (values.Length != method.ParamSig.Count - offset) return false;
        for (int i = 0; i < values.Length; i++) {
            if (!CanConvert(values[i], method.ParamSig[i + offset].Type) || !IsOrdinaryArgument(values[i])) return false;
        }

        // Receiver and argument conversion precede the protected invocation;
        // reflection does not wrap errors raised while preparing the call.
        var builder = BuilderBefore(reflection);
        var arguments = new Value[method.ParamSig.Count];
        if (method.IsInstance) arguments[0] = ConvertReceiver(builder, receiver, method.DeclaringType);
        for (int i = 0; i < values.Length; i++) {
            arguments[i + offset] = ConvertArgument(builder, values[i], method.ParamSig[i + offset].Type);
        }

        var continuation = wrapExceptions ? CreateExceptionWrapper(builder, reflection) : null;
        var direct = builder.Emit(new CallInst(method, arguments, isVirtual: method.IsInstance && !method.DeclaringType.IsValueType) {
            InBounds = true,
            DebugLocation = reflection.DebugLocation
        });
        var result = method.ReturnType == PrimType.Void ? ConstNull.Create() : BoxResult(builder, direct);
        if (continuation != null) builder.Emit(new LeaveInst(continuation));
        if (reflection.ResultType == PrimType.Void) reflection.Remove();
        else reflection.ReplaceWith(result);
        return true;
    }

    private bool CanInvokeSignature(MethodDefOrSpec method)
    {
        if (method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute")) return false;
        if (method.ReturnType != PrimType.Void && !_types.IsBoxable(method.ReturnType)) return false;
        int offset = method.IsInstance ? 1 : 0;
        return !method.ReturnSig.HasModifiers && !method.ParamSig.Skip(offset).Any(sig => sig.HasModifiers);
    }

    private bool CanWrapInvocation(CallInst reflection)
    {
        // A catch in this module must see non-Exception throws as reflection's catch does.
        if (_ctx.Compilation.WrapsNonExceptions(_ctx.Module) != _ctx.Compilation.WrapsNonExceptions(_ctx.Module.Resolver.CoreLib)) return false;
        // CIL exception filters cannot contain a nested try/catch.
        return !_ctx.GetAnalysis<ProtectedRegionAnalysis>().GetBlockRegion(reflection.Block).IsFilter;
    }

    private BasicBlock CreateExceptionWrapper(IRBuilder builder, CallInst reflection)
    {
        var previous = reflection.Block;
        var continuation = previous.Split(reflection);
        var body = _ctx.Method.CreateBlock(insertAfter: previous);
        var handler = _ctx.Method.CreateBlock(insertAfter: body);
        previous.SetBranch(body);

        var exceptionType = _ctx.Module.Resolver.Import(typeof(Exception));
        var guard = new GuardInst(GuardKind.Catch, handler, exceptionType);
        body.InsertFirst(guard);
        var caught = new IRBuilder(handler);
        var constructor = _ctx.Module.Resolver.Import(typeof(TargetInvocationException))
            .FindMethod(".ctor", new MethodSig(PrimType.Void, [exceptionType], true));
        caught.Emit(new ThrowInst(caught.CreateNewObj(constructor, guard)));

        builder.SetPosition(body, InsertionDir.After);
        return continuation;
    }

    private bool IsOrdinaryArgument(Value value)
    {
        // Invoke substitutes optional defaults for Missing, including object parameters.
        if (value is ConstNull or CilIntrinsic.Box { SourceType.IsValueType: true }) return true;
        if (value is CilIntrinsic.Box box) return !_types.CanContainMissing(box.SourceType);
        var missing = _types.Resolver.Import(typeof(Missing));
        return TypeUtils.HasConcreteType(value) ? value.ResultType != missing : !_types.CanContainMissing(value.ResultType);
    }
}
