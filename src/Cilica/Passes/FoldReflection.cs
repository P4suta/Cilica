namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

/// <summary> Replaces statically resolved reflection with direct member operations. </summary>
public sealed partial class FoldReflection : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx)
    {
        long revision = ctx.Method.Revision;
        long controlFlowRevision = ctx.Method.ControlFlowRevision;
        var types = new ReflectionTypeFacts(ctx.Definition);
        var lookups = new ReflectionLookup(types);
        var access = new InliningAccess(ctx.Definition.DeclaringType,
            ignoreVisibility: ctx.Compilation.Module != ctx.Compilation.Resolver.CoreLib);

        foreach (var call in ctx.Method.Instructions().OfType<CallInst>().ToArray()) {
            if (call.IsTailCall || call.IsReadOnly || call.Constraint != null) continue;

            if (!ctx.Compilation.Settings.PreserveTrimmingDependencies && call.Method.Name == "MakeGenericType" &&
                lookups.ResolveType(call) is { } constructedType && access.CanUseType(constructedType)) {
                var original = ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
                var builder = new IRBuilder(call, InsertionDir.Before).SetNextDebugLocation(call.DebugLocation);
                var typeType = ctx.Module.Resolver.Import(typeof(Type));
                var handle = builder.Emit(new CilIntrinsic.LoadHandle(ctx.Module.Resolver, constructedType));
                var replacement = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle",
                    new MethodSig(typeType, [handle.ResultType], false)), handle);
                call.ReplaceUses(replacement);
                lookups.ResolveType(replacement);
                ctx.Logger.Trace($"Folded reflection type construction '{original}' in '{call.Block}' to '{constructedType}'.");
                continue;
            }

            var creation = DelegateCreation.Match(call, ctx.Module.Resolver, lookups);
            if (creation == null && !IsMemberAccess(call)) continue;
            Value? source = creation?.MethodInfo ?? (call.IsStatic ? null : call.Args[0]);
            var resolved = source == null ? null : lookups.Resolve(source);
            if (resolved == null && GetFunctionPointerTarget(call) is { } pointerTarget) {
                resolved = new(pointerTarget, pointerTarget.DeclaringType);
            }
            if (resolved == null) continue;

            var originalCall = ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
            var block = call.Block;
            bool changed = resolved.Member switch {
                MethodDefOrSpec method when creation != null => FoldDelegate(call, creation, method, access, types),
                MethodDefOrSpec method when IsFunctionPointerCall(call) => FoldFunctionPointer(call, method, access, types),
                MethodDefOrSpec method when IsMethodHandleCall(call) => FoldMethodHandle(ctx, call, method, access, types),
                FieldDesc field => FoldField(call, field, access, types),
                MethodDefOrSpec method => FoldMethod(ctx, call, method, access, types),
                PropertyDef property => FoldProperty(ctx, call, property, resolved.ReflectedType, access, types),
                _ => false
            };
            if (changed) {
                ctx.Logger.Trace($"Folded reflection access '{originalCall}' in '{block}' to '{resolved.Member}'.");
            }
        }

        if (!ctx.Compilation.Settings.PreserveTrimmingDependencies) {
            lookups.RemoveUnused();
        }
        return ctx.Method.Revision == revision ? MethodInvalidations.None :
            ctx.Method.ControlFlowRevision == controlFlowRevision ? MethodInvalidations.DataFlow : MethodInvalidations.ExceptionRegions;
    }

    private static bool IsMemberAccess(CallInst call)
        => !call.IsStatic && (call.Method.Name switch {
            "GetValue" or "SetValue" => call.Method.DeclaringType.IsCorelibType(typeof(FieldInfo)) ||
                call.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)),
            "Invoke" => call.Method.DeclaringType.IsCorelibType(typeof(MethodBase)),
            "get_MethodHandle" => IsMethodHandleCall(call),
            "GetFunctionPointer" => IsFunctionPointerCall(call),
            _ => false
        });

    private static bool FoldField(CallInst call, FieldDesc field, InliningAccess access, ReflectionTypeFacts types)
    {
        bool load = Matches(call, typeof(FieldInfo), "GetValue", PrimType.Object, PrimType.Object);
        bool store = Matches(call, typeof(FieldInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object);
        if (!load && !store || !access.CanUseMember(field) || !types.IsBoxable(field.Type) ||
            field.DeclaringType.IsCorelibType(typeof(Nullable<>)) || field.Attribs.HasFlag(FieldAttributes.Literal) ||
            store && (field.Attribs.HasFlag(FieldAttributes.InitOnly) || !CanStoreField(field.Type, types)) ||
            !field.IsStatic && !CanUseReceiver(call.Args[1], field.DeclaringType, types) ||
            store && !CanConvert(call.Args[2], field.Type, types)) return false;

        var builder = new IRBuilder(call, InsertionDir.Before).SetNextDebugLocation(call.DebugLocation);
        var receiver = field.IsStatic ? null : ConvertReceiver(builder, call.Args[1], field.DeclaringType);
        var address = builder.CreateFieldAddr(field, receiver, inBounds: true);
        // Reflection's field accessors use acquire/release accesses, including
        // reference fields without an IsVolatile modifier.
        if (load) {
            var value = builder.CreateLoad(address, flags: PointerFlags.Volatile);
            call.ReplaceWith(BoxResult(builder, value));
        } else {
            var value = ConvertArgument(builder, call.Args[2], field.Type);
            builder.CreateStore(address, value, flags: PointerFlags.Volatile);
            call.Remove();
        }
        return true;
    }

    private static bool FoldMethod(MethodTransformContext ctx, CallInst call, MethodDefOrSpec method, InliningAccess access, ReflectionTypeFacts types)
    {
        bool wrap;
        Value arguments;
        if (Matches(call, typeof(MethodBase), "Invoke", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray())) {
            wrap = true;
            arguments = call.Args[2];
        } else if (Matches(call, typeof(MethodBase), "Invoke", PrimType.Object, PrimType.Object,
            ctx.Module.Resolver.Import(typeof(BindingFlags)), ctx.Module.Resolver.Import(typeof(Binder)),
            PrimType.Object.CreateArray(), ctx.Module.Resolver.Import(typeof(System.Globalization.CultureInfo))) &&
            call.Args[2] is ConstInt flags && ((BindingFlags)flags.Value & ~BindingFlags.DoNotWrapExceptions) == 0 &&
            call.Args[3] is ConstNull && call.Args[5] is ConstNull) {
            wrap = ((BindingFlags)flags.Value & BindingFlags.DoNotWrapExceptions) == 0;
            arguments = call.Args[4];
        } else return false;

        int count = method.ParamSig.Count - (method.IsInstance ? 1 : 0);
        if (!KnownArray.TryRead(arguments, call, count, out var array)) return false;
        if (!FoldInvocation(ctx, call, method, call.Args[1], array.Values, wrap, access, types)) return false;
        array.Remove();
        return true;
    }

    private static bool FoldProperty(MethodTransformContext ctx, CallInst call, PropertyDef property,
        TypeDefOrSpec reflectedType, InliningAccess access, ReflectionTypeFacts types)
    {
        bool load = Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        bool store = Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        if (!load && !store || property.Sig.ParamTypes.Count != 0) return false;
        var accessor = (load ? property.Getter : property.Setter)?.GetSpec(new GenericContext(reflectedType));
        if (accessor == null) return false;

        KnownArray? index = null;
        if (call.NumArgs == (load ? 3 : 4) && !KnownArray.TryRead(call.Args[^1], call, 0, out index)) return false;
        if (!FoldInvocation(ctx, call, accessor, call.Args[1], load ? [] : [call.Args[2]], wrap: true, access, types)) return false;
        index?.Remove();
        return true;
    }

    private static bool FoldInvocation(MethodTransformContext ctx, CallInst reflection, MethodDefOrSpec method,
        Value receiver, Value[] values, bool wrap, InliningAccess access, ReflectionTypeFacts types)
    {
        int offset = method.IsInstance ? 1 : 0;
        if (!ReflectionTypeFacts.IsConstructedMethod(method) || method.CallConv != CallConvention.Managed || method.IsRuntimeAsync ||
            method.IsStatic && method.Attribs.HasFlag(MethodAttributes.Virtual) ||
            !types.IsBoxable(method.DeclaringType) || method.DeclaringType.IsCorelibType(typeof(Nullable<>)) ||
            method.ReturnType != PrimType.Void && !types.IsBoxable(method.ReturnType) ||
            method.ReturnSig.HasModifiers || method.ParamSig.Skip(offset).Any(sig => sig.HasModifiers) ||
            method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") ||
            Compilation.IsFrameSensitive(method) || TypeUtils.HasClassMethodImpls(method) ||
            !access.CanUseMember(method) || !method.IsStatic && !CanUseReceiver(receiver, method.DeclaringType, types) ||
            values.Length != method.ParamSig.Count - offset) return false;
        // A catch in this module must see non-Exception throws as reflection's catch does.
        if (wrap && ctx.Compilation.WrapsNonExceptions(ctx.Module) != ctx.Compilation.WrapsNonExceptions(ctx.Module.Resolver.CoreLib)) return false;
        // CIL exception filters cannot contain a nested try/catch.
        if (wrap && ctx.GetAnalysis<ProtectedRegionAnalysis>().GetBlockRegion(reflection.Block).IsFilter) return false;
        for (int i = 0; i < values.Length; i++) {
            if (!CanConvert(values[i], method.ParamSig[i + offset].Type, types) || !IsOrdinaryArgument(values[i], types)) return false;
        }

        var builder = new IRBuilder(reflection, InsertionDir.Before).SetNextDebugLocation(reflection.DebugLocation);
        var arguments = new Value[method.ParamSig.Count];
        if (method.IsInstance) arguments[0] = ConvertReceiver(builder, receiver, method.DeclaringType);
        for (int i = 0; i < values.Length; i++) {
            arguments[i + offset] = ConvertArgument(builder, values[i], method.ParamSig[i + offset].Type);
        }

        BasicBlock? continuation = null;
        if (wrap) {
            var previous = reflection.Block;
            continuation = previous.Split(reflection);
            var body = ctx.Method.CreateBlock(insertAfter: previous);
            var handler = ctx.Method.CreateBlock(insertAfter: body);
            previous.SetBranch(body);
            var exceptionType = ctx.Module.Resolver.Import(typeof(Exception));
            var guard = new GuardInst(GuardKind.Catch, handler, exceptionType);
            body.InsertFirst(guard);
            var caught = new IRBuilder(handler);
            var constructor = ctx.Module.Resolver.Import(typeof(TargetInvocationException))
                .FindMethod(".ctor", new MethodSig(PrimType.Void, [exceptionType], true));
            caught.Emit(new ThrowInst(caught.CreateNewObj(constructor, guard)));
            builder.SetPosition(body, InsertionDir.After);
        }

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

    private static bool Matches(CallInst call, Type owner, string name, TypeDesc result, params TypeSig[] parameters)
        => call.Method.Name == name && call.Method.DeclaringType.IsCorelibType(owner) &&
            new MethodSig(result, parameters, true).Matches(call.Method);

    private static bool CanUseReceiver(Value receiver, TypeDesc owner, ReflectionTypeFacts types)
        => owner.IsValueType
            ? receiver is CilIntrinsic.Box box && box.SourceType == owner && types.IsNonNull(receiver)
            : types.IsNonNull(receiver) && types.IsAssignable(receiver is CilIntrinsic.Box boxed ? boxed.SourceType : receiver.ResultType, owner) &&
                (receiver is not NewObjInst || TypeUtils.HasConcreteType(receiver));

    private static Value ConvertReceiver(IRBuilder builder, Value receiver, TypeDesc owner)
        => owner.IsValueType ? builder.CreateUnboxRef(owner, receiver) : receiver.ResultType.IsAssignableTo(owner) ? receiver
            : builder.Emit(new CilIntrinsic.RefCast(owner, receiver));

    private static bool CanConvert(Value value, TypeDesc type, ReflectionTypeFacts types)
        => types.IsBoxable(type) && (type.IsValueType || type is GenericParamType
            ? !type.IsCorelibType(typeof(Nullable<>)) && value is CilIntrinsic.Box box && box.SourceType == type
            : value is ConstNull || types.IsAssignable(value.ResultType, type));

    private static bool IsOrdinaryArgument(Value value, ReflectionTypeFacts types)
    {
        // Invoke substitutes optional defaults for Missing, including object parameters.
        if (value is ConstNull or CilIntrinsic.Box { SourceType.IsValueType: true }) return true;
        if (value is CilIntrinsic.Box box) return !types.CanContainMissing(box.SourceType);
        var missing = types.Resolver.Import(typeof(Missing));
        return TypeUtils.HasConcreteType(value) ? value.ResultType != missing : !types.CanContainMissing(value.ResultType);
    }

    private static bool CanStoreField(TypeDesc type, ReflectionTypeFacts types)
        // Reflection can use atomic 64-bit writes even on 32-bit targets. A volatile
        // IL store does not establish that contract for wide values or arbitrary structs.
        => type is GenericParamType ? types.IsReferenceType(type) : type.Kind is not (TypeKind.Int64 or TypeKind.UInt64 or TypeKind.Double or TypeKind.Struct);

    private static Value ConvertArgument(IRBuilder builder, Value value, TypeDesc type)
        => type.IsValueType || type is GenericParamType ? builder.CreateUnboxObj(type, value) : value;

    private static Value BoxResult(IRBuilder builder, Value value)
        => value.ResultType.IsValueType || value.ResultType is GenericParamType ? builder.CreateBox(value.ResultType, value) : value;
}
