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
        var lookups = new Dictionary<CallInst, ReflectionLookup.Result?>();
        var typeValues = new HashSet<CallInst>();
        var access = new InliningAccess(ctx.Definition.DeclaringType);

        foreach (var call in ctx.Method.Instructions().OfType<CallInst>().ToArray()) {
            if (call.IsTailCall || call.IsReadOnly || call.Constraint != null) continue;

            var creation = DelegateCreation.Match(call, ctx.Module.Resolver);
            if (creation == null && !IsMemberAccess(call)) continue;
            Value? source = creation?.MethodInfo ?? (call.IsStatic ? null : call.Args[0]);
            ReflectionLookup.Result? resolved = null;
            if (source is CallInst lookup && !lookups.TryGetValue(lookup, out resolved)) {
                lookups.Add(lookup, resolved = ReflectionLookup.Resolve(lookup));
            }
            if (resolved == null && GetFunctionPointerTarget(call) is { } pointerTarget) {
                resolved = new(pointerTarget, pointerTarget.DeclaringType);
            }
            if (resolved == null) continue;

            var originalCall = ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
            var block = call.Block;
            bool changed = resolved.Member switch {
                MethodDefOrSpec method when creation != null => FoldDelegate(call, creation, method, access),
                MethodDefOrSpec method when IsFunctionPointerCall(call) => FoldFunctionPointer(call, method, access),
                MethodDefOrSpec method when IsMethodHandleCall(call) => FoldMethodHandle(ctx, call, method, access),
                FieldDesc field => FoldField(call, field, access),
                MethodDefOrSpec method => FoldMethod(ctx, call, method, access),
                PropertyDef property => FoldProperty(ctx, call, property, resolved.ReflectedType, access),
                _ => false
            };
            if (changed) {
                ctx.Logger.Trace($"Folded reflection access '{originalCall}' in '{block}' to '{resolved.Member}'.");
                if (creation?.TypeValue is CallInst typeValue) typeValues.Add(typeValue);
            }
        }

        if (!ctx.Compilation.Settings.PreserveTrimmingDependencies) {
            foreach (var (lookup, resolved) in lookups) {
                if (resolved == null || lookup.NumUses != 0) continue;
                typeValues.Add((CallInst)lookup.Args[0]);
                lookup.Remove();
            }
            foreach (var typeValue in typeValues) {
                // GetType on a potentially null receiver still performs validation.
                if (typeValue.NumUses == 0 && (typeValue.Method.Name == "GetTypeFromHandle" ||
                    IsNonNull(typeValue.Args[0]))) typeValue.Remove();
            }
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

    private static bool FoldField(CallInst call, FieldDesc field, InliningAccess access)
    {
        bool load = Matches(call, typeof(FieldInfo), "GetValue", PrimType.Object, PrimType.Object);
        bool store = Matches(call, typeof(FieldInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object);
        if (!load && !store || !access.CanUseMember(field) || !IsBoxable(field.Type) ||
            field.DeclaringType.IsCorelibType(typeof(Nullable<>)) || field.Attribs.HasFlag(FieldAttributes.Literal) ||
            store && (field.Attribs.HasFlag(FieldAttributes.InitOnly) || !CanStoreField(field.Type)) ||
            !field.IsStatic && !CanUseReceiver(call.Args[1], field.DeclaringType) ||
            store && !CanConvert(call.Args[2], field.Type)) return false;

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

    private static bool FoldMethod(MethodTransformContext ctx, CallInst call, MethodDefOrSpec method, InliningAccess access)
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
        if (!ArgumentArray.TryRead(arguments, call, count, out var array)) return false;
        if (!FoldInvocation(ctx, call, method, call.Args[1], array.Values, wrap, access)) return false;
        array.Remove();
        return true;
    }

    private static bool FoldProperty(MethodTransformContext ctx, CallInst call, PropertyDef property,
        TypeDefOrSpec reflectedType, InliningAccess access)
    {
        bool load = Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        bool store = Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        if (!load && !store || property.Sig.ParamTypes.Count != 0) return false;
        var accessor = (load ? property.Getter : property.Setter)?.GetSpec(new GenericContext(reflectedType));
        if (accessor == null) return false;

        ArgumentArray? index = null;
        if (call.NumArgs == (load ? 3 : 4) && !ArgumentArray.TryRead(call.Args[^1], call, 0, out index)) return false;
        if (!FoldInvocation(ctx, call, accessor, call.Args[1], load ? [] : [call.Args[2]], wrap: true, access)) return false;
        index?.Remove();
        return true;
    }

    private static bool FoldInvocation(MethodTransformContext ctx, CallInst reflection, MethodDefOrSpec method,
        Value receiver, Value[] values, bool wrap, InliningAccess access)
    {
        int offset = method.IsInstance ? 1 : 0;
        if (method.IsGeneric || method.CallConv != CallConvention.Managed || method.IsRuntimeAsync ||
            !IsBoxable(method.DeclaringType) || method.DeclaringType.IsCorelibType(typeof(Nullable<>)) ||
            method.ReturnType != PrimType.Void && !IsBoxable(method.ReturnType) ||
            method.ReturnSig.HasModifiers || method.ParamSig.Skip(offset).Any(sig => sig.HasModifiers) ||
            method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") ||
            Compilation.IsFrameSensitive(method) || TypeUtils.HasClassMethodImpls(method) ||
            !access.CanUseMember(method) || !method.IsStatic && !CanUseReceiver(receiver, method.DeclaringType) ||
            values.Length != method.ParamSig.Count - offset) return false;
        // A catch in this module must see non-Exception throws as reflection's catch does.
        if (wrap && ctx.Compilation.WrapsNonExceptions(ctx.Module) != ctx.Compilation.WrapsNonExceptions(ctx.Module.Resolver.CoreLib)) return false;
        // CIL exception filters cannot contain a nested try/catch.
        if (wrap && ctx.GetAnalysis<ProtectedRegionAnalysis>().GetBlockRegion(reflection.Block).IsFilter) return false;
        for (int i = 0; i < values.Length; i++) {
            if (!CanConvert(values[i], method.ParamSig[i + offset].Type) || !IsOrdinaryArgument(values[i], ctx.Module.Resolver)) return false;
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

    private static bool IsBoxable(TypeDesc type)
        => !type.IsUnboundGeneric && type is not (PointerType or FuncPtrType) &&
            type.Kind is not (TypeKind.Void or TypeKind.TypedRef) &&
            !(type is TypeDefOrSpec nominal && nominal.HasCustomAttrib("System.Runtime.CompilerServices", "IsByRefLikeAttribute"));

    private static bool IsNonNull(Value value)
        => value is ConstString || ConstFolding.FoldCondition(value) == true;

    private static bool CanUseReceiver(Value receiver, TypeDesc owner)
        => owner.IsValueType
            ? receiver is CilIntrinsic.Box box && box.SourceType == owner && IsNonNull(receiver)
            : IsNonNull(receiver) && receiver.ResultType.IsAssignableTo(owner) &&
                (receiver is not NewObjInst || TypeUtils.HasConcreteType(receiver));

    private static Value ConvertReceiver(IRBuilder builder, Value receiver, TypeDesc owner)
        => owner.IsValueType ? builder.CreateUnboxRef(owner, receiver) : receiver;

    private static bool CanConvert(Value value, TypeDesc type)
        => IsBoxable(type) && (type.IsValueType
            ? !type.IsCorelibType(typeof(Nullable<>)) && value is CilIntrinsic.Box box && box.SourceType == type
            : value is ConstNull || value.ResultType.IsAssignableTo(type));

    private static bool IsOrdinaryArgument(Value value, ModuleResolver resolver)
    {
        // Invoke substitutes optional defaults for Missing, including object parameters.
        if (value is ConstNull or CilIntrinsic.Box { SourceType.IsValueType: true }) return true;
        var missing = resolver.Import(typeof(Missing));
        return TypeUtils.HasConcreteType(value) ? value.ResultType != missing : !missing.IsAssignableTo(value.ResultType);
    }

    private static bool CanStoreField(TypeDesc type)
        // Reflection can use atomic 64-bit writes even on 32-bit targets. A volatile
        // IL store does not establish that contract for wide values or arbitrary structs.
        => type.Kind is not (TypeKind.Int64 or TypeKind.UInt64 or TypeKind.Double or TypeKind.Struct);

    private static Value ConvertArgument(IRBuilder builder, Value value, TypeDesc type)
        => type.IsValueType ? builder.CreateUnboxObj(type, value) : value;

    private static Value BoxResult(IRBuilder builder, Value value)
        => value.ResultType.IsValueType ? builder.CreateBox(value.ResultType, value) : value;

    // A private, straight-line object[] can be read at the invocation point.
    // Boxed values remain objects until conversion there, since their contents can mutate.
    private sealed record ArgumentArray(Value[] Values, Instruction? Allocation, Instruction[] Stores, Instruction[] Addresses)
    {
        public static bool TryRead(Value value, CallInst consumer, int count, out ArgumentArray result)
        {
            result = null!;
            if (count == 0 && (value is ConstNull || value is CallInst { Method.Name: "Empty", NumArgs: 0 } empty &&
                empty.Method.DeclaringType.IsCorelibType(typeof(Array)) && empty.Method.GenericParams is [var element] && element == PrimType.Object)) {
                result = new([], value as Instruction, [], []);
                return true;
            }
            if (value is not CilIntrinsic.NewArray { StaticArgs.Length: 0 } allocation || allocation.ElemType != PrimType.Object ||
                allocation.Args[0] is not ConstInt length || length.Value != count || allocation.Block != consumer.Block) return false;

            var addresses = new HashSet<Instruction>();
            var stores = new HashSet<Instruction>();
            int consumerUses = 0;
            foreach (var (user, operand) in allocation.Uses()) {
                if (user == consumer) { consumerUses++; continue; }
                if (user is not ArrayAddrInst address || operand != 0 || address.ElemType != PrimType.Object ||
                    address.Index is not ConstInt index || index.Value < 0 || index.Value >= count) return false;
                addresses.Add(address);
                foreach (var (use, position) in address.Uses()) {
                    if (use is not StoreInst store || position != 0 || store.ElemType != PrimType.Object || store.Flags != PointerFlags.None) return false;
                    stores.Add(store);
                }
            }
            if (consumerUses != 1) return false;

            var values = Enumerable.Repeat<Value>(ConstNull.Create(), count).ToArray();
            var remaining = new HashSet<Instruction>(stores.Concat(addresses));
            for (var instruction = allocation.Next!; instruction != consumer; instruction = instruction.Next!) {
                if (!remaining.Remove(instruction)) continue;
                if (instruction is StoreInst store) values[(int)((ConstInt)((ArrayAddrInst)store.Address).Index).Value] = store.Value;
            }
            if (remaining.Count != 0) return false;
            result = new(values, allocation, stores.ToArray(), addresses.ToArray());
            return true;
        }

        public void Remove()
        {
            foreach (var store in Stores) store.Remove();
            foreach (var address in Addresses) address.Remove();
            if (Allocation?.NumUses == 0) Allocation.Remove();
        }
    }
}
