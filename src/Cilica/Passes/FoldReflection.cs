namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

/// <summary> Replaces statically resolved reflection with direct member operations. </summary>
public sealed class FoldReflection : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx) => new ReflectionRewriter(ctx).Run();
}

// Lookup facts and access checks belong to one method transformation. The pass
// itself is stateless and can be reused by the compilation pipeline.
internal sealed partial class ReflectionRewriter
{
    readonly MethodTransformContext _ctx;
    readonly ReflectionTypeFacts _types;
    readonly ReflectionLookup _lookups;
    readonly InliningAccess _access;

    public ReflectionRewriter(MethodTransformContext ctx)
    {
        _ctx = ctx;
        _types = new ReflectionTypeFacts(ctx.Definition);
        _lookups = new ReflectionLookup(_types);
        _access = new InliningAccess(ctx.Definition.DeclaringType,
            ignoreVisibility: ctx.Compilation.Module != ctx.Compilation.Resolver.CoreLib);
    }

    public MethodPassResult Run()
    {
        long revision = _ctx.Method.Revision;
        long controlFlowRevision = _ctx.Method.ControlFlowRevision;

        foreach (var call in _ctx.Method.Instructions().OfType<CallInst>().ToArray()) {
            if (call.IsTailCall || call.IsReadOnly || call.Constraint != null) continue;
            FoldCall(call);
        }

        _lookups.ApplyFacts(_ctx.Compilation.Settings.PreserveTrimmingDependencies, _ctx.Logger);
        if (_ctx.Method.Revision == revision) return MethodInvalidations.None;
        return _ctx.Method.ControlFlowRevision == controlFlowRevision
            ? MethodInvalidations.DataFlow : MethodInvalidations.ExceptionRegions;
    }

    private void FoldCall(CallInst call)
    {
        if (call.Method.Name == "MakeGenericType" && _lookups.ResolveType(call) is { } constructedType) {
            FoldTypeConstruction(call, constructedType);
            return;
        }
        if (ReflectionLookup.IsMemberLookup(call)) {
            _lookups.Resolve(call);
            return;
        }
        if (ReflectionLookup.IsTypeLookup(call)) {
            _lookups.ResolveType(call);
            return;
        }

        var creation = DelegateCreation.Match(call, _ctx.Module.Resolver, _lookups);
        if (creation == null && !IsMemberAccess(call)) return;

        Value? source = creation?.MethodInfo ?? (call.IsStatic ? null : call.Args[0]);
        var resolved = source == null ? null : _lookups.Resolve(source);
        if (resolved == null && GetFunctionPointerTarget(call) is { } pointerTarget) {
            resolved = new(pointerTarget, pointerTarget.DeclaringType);
        }
        if (resolved == null) return;

        var original = _ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
        var block = call.Block;
        bool changed = resolved.Member switch {
            MethodDefOrSpec method when creation != null => FoldDelegate(call, creation, method),
            MethodDefOrSpec method when IsFunctionPointerCall(call) => FoldFunctionPointer(call, method),
            MethodDefOrSpec method when IsMethodHandleCall(call) => FoldMethodHandle(call, method),
            FieldDesc field => FoldField(call, field),
            MethodDefOrSpec method => FoldMethod(call, method),
            PropertyDef property => FoldProperty(call, property, resolved.ReflectedType),
            _ => false
        };
        if (changed && call.Block == null) {
            _ctx.Logger.Trace($"Folded reflection access '{original}' in '{block}' to '{resolved.Member}'.");
        }
    }

    private void FoldTypeConstruction(CallInst call, TypeDesc type)
    {
        if (_ctx.Compilation.Settings.PreserveTrimmingDependencies || !_access.CanUseType(type)) return;

        var original = _ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
        var builder = BuilderBefore(call);
        var typeType = _ctx.Module.Resolver.Import(typeof(Type));
        var handle = builder.Emit(new CilIntrinsic.LoadHandle(_ctx.Module.Resolver, type));
        var replacement = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle",
            new MethodSig(typeType, [handle.ResultType], false)), handle);
        call.ReplaceUses(replacement);
        _lookups.ResolveType(replacement);
        _ctx.Logger.Trace($"Folded reflection type construction '{original}' in '{call.Block}' to '{type}'.");
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

    private bool FoldField(CallInst call, FieldDesc field)
    {
        bool load = Matches(call, typeof(FieldInfo), "GetValue", PrimType.Object, PrimType.Object);
        bool store = Matches(call, typeof(FieldInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object);
        if (!load && !store) return false;
        if (!_access.CanUseMember(field) || !_types.IsBoxable(field.Type)) return false;
        if (field.DeclaringType.IsCorelibType(typeof(Nullable<>)) || field.Attribs.HasFlag(FieldAttributes.Literal)) return false;
        if (!field.IsStatic && !CanUseReceiver(call.Args[1], field.DeclaringType)) return false;

        if (store) {
            if (field.Attribs.HasFlag(FieldAttributes.InitOnly) || !CanStoreField(field.Type)) return false;
            if (!CanConvert(call.Args[2], field.Type)) return false;
        }

        var builder = BuilderBefore(call);
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

    // Both invocation and method-address folding require an ordinary, accessible
    // runtime method. Their signature and binding rules are checked separately.
    private bool CanReferenceMethod(MethodDefOrSpec method)
    {
        if (!ReflectionTypeFacts.IsConstructedMethod(method)) return false;
        if (method.CallConv != CallConvention.Managed || method.IsRuntimeAsync) return false;
        if (method.IsStatic && method.Attribs.HasFlag(MethodAttributes.Virtual)) return false;
        if (!_types.IsBoxable(method.DeclaringType) || method.DeclaringType.IsCorelibType(typeof(Nullable<>))) return false;

        // Emitting the member directly does not reproduce reflection's caller
        // frame or redirects established by class MethodImpls.
        if (Compilation.IsFrameSensitive(method) || TypeUtils.HasClassMethodImpls(method)) return false;
        return _access.CanUseMember(method);
    }

    private static bool Matches(CallInst call, Type owner, string name, TypeDesc result, params TypeSig[] parameters)
        => call.Method.Name == name && call.Method.DeclaringType.IsCorelibType(owner) &&
            new MethodSig(result, parameters, true).Matches(call.Method);

    private static IRBuilder BuilderBefore(Instruction instruction)
        => new IRBuilder(instruction, InsertionDir.Before).SetNextDebugLocation(instruction.DebugLocation);

    private bool CanUseReceiver(Value receiver, TypeDesc owner)
    {
        if (!_types.IsNonNull(receiver)) return false;
        if (owner.IsValueType) return receiver is CilIntrinsic.Box box && box.SourceType == owner;

        var receiverType = receiver is CilIntrinsic.Box boxed ? boxed.SourceType : receiver.ResultType;
        return _types.IsAssignable(receiverType, owner) &&
            (receiver is not NewObjInst || TypeUtils.HasConcreteType(receiver));
    }

    private static Value ConvertReceiver(IRBuilder builder, Value receiver, TypeDesc owner)
    {
        if (owner.IsValueType) return builder.CreateUnboxRef(owner, receiver);
        if (receiver.ResultType.IsAssignableTo(owner)) return receiver;
        return builder.Emit(new CilIntrinsic.RefCast(owner, receiver));
    }

    private bool CanConvert(Value value, TypeDesc type)
    {
        if (!_types.IsBoxable(type)) return false;
        if (type.IsValueType || type is GenericParamType) {
            return !type.IsCorelibType(typeof(Nullable<>)) && value is CilIntrinsic.Box box && box.SourceType == type;
        }
        return value is ConstNull || _types.IsAssignable(value.ResultType, type);
    }

    private bool CanStoreField(TypeDesc type)
        // Reflection can use atomic 64-bit writes even on 32-bit targets. A volatile
        // IL store does not establish that contract for wide values or arbitrary structs.
        => type is GenericParamType ? _types.IsReferenceType(type)
            : type.Kind is not (TypeKind.Int64 or TypeKind.UInt64 or TypeKind.Double or TypeKind.Struct);

    private static Value ConvertArgument(IRBuilder builder, Value value, TypeDesc type)
        => type.IsValueType || type is GenericParamType ? builder.CreateUnboxObj(type, value) : value;

    private static Value BoxResult(IRBuilder builder, Value value)
        => value.ResultType.IsValueType || value.ResultType is GenericParamType ? builder.CreateBox(value.ResultType, value) : value;
}
