namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Proves reflection type arguments and constraints in the containing generic context. </summary>
internal sealed class ReflectionTypeFacts(MethodDef caller)
{
    readonly GenericContext _context = new(caller);
    readonly ModuleResolver _resolver = caller.Module.Resolver;
    public ModuleResolver Resolver => _resolver;

    public bool IsNonNull(Value value)
        => value is ConstString || IR.Utils.ConstFolding.FoldCondition(value) == true ||
            value is CilIntrinsic.Box box && IsNonNullableValueType(box.SourceType);

    // A signature variable denotes a concrete runtime argument. An unspecialized
    // generic definition, including one nested in a constructed type, stays open.
    public static bool IsRuntimeType(TypeDesc type)
        => type switch {
            TypeDef definition => !definition.IsGeneric,
            TypeSpec specification => specification.GenericParams.All(IsRuntimeType),
            CompoundType compound => IsRuntimeType(compound.ElemType),
            FuncPtrType pointer => IsRuntimeType(pointer.Signature.ReturnType.Type) && pointer.Signature.ParamTypes.All(p => IsRuntimeType(p.Type)),
            _ => true
        };

    public static bool IsConstructedMethod(MethodDefOrSpec method)
        => IsRuntimeType(method.DeclaringType) &&
            (!method.IsGeneric || method is MethodSpec { IsBoundGeneric: true } && method.GenericParams.All(IsRuntimeType));

    public bool IsBoxable(TypeDesc type)
        => IsRuntimeType(type) && type is not (PointerType or FuncPtrType) &&
            type.Kind is not (TypeKind.Void or TypeKind.TypedRef) && !MayBeByRefLike(type);

    public bool MayBeByRefLike(TypeDesc type)
        => type is GenericParamType parameter ? Resolve(parameter).Attribs.HasFlag(GenericParameterAttributes.AllowByRefLike)
            : type is TypeDefOrSpec nominal && nominal.HasCustomAttrib("System.Runtime.CompilerServices", "IsByRefLikeAttribute");

    public bool IsNonNullableValueType(TypeDesc type)
        => type is GenericParamType parameter ? Resolve(parameter).Attribs.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)
            : type.IsValueType && !type.IsCorelibType(typeof(Nullable<>));

    public bool IsReferenceType(TypeDesc type)
    {
        if (type is not GenericParamType parameter) return type.IsManagedObject() && !type.IsValueType;
        parameter = Resolve(parameter);
        return parameter.Attribs.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) ||
            parameter.Constraints.Any(constraint => ConstraintRequiresReference(constraint.Sig.Type, new() { type }));
    }

    private bool ConstraintRequiresReference(TypeDesc type, HashSet<TypeDesc> visited)
    {
        if (type is GenericParamType parameter) {
            if (!visited.Add(type)) return false;
            // T : U, U : class can still admit T=int, U=object. Only a
            // concrete class bound on U rules out boxed value-type arguments.
            return Resolve(parameter).Constraints.Any(constraint => ConstraintRequiresReference(constraint.Sig.Type, visited));
        }
        return Normalize(type) is TypeDefOrSpec { IsClass: true, IsValueType: false } nominal &&
            nominal != PrimType.Object && !nominal.IsCorelibType(typeof(ValueType)) && !nominal.IsCorelibType(typeof(Enum));
    }

    public bool SatisfiesConstraints(IReadOnlyList<GenericParamType> parameters, IReadOnlyList<TypeDesc> arguments, GenericContext instantiation)
    {
        for (int i = 0; i < parameters.Count; i++) {
            var parameter = parameters[i];
            var argument = arguments[i];
            if (!IsRuntimeType(argument) || argument is PointerType or FuncPtrType || argument.Kind is TypeKind.Void or TypeKind.TypedRef ||
                MayBeByRefLike(argument) && !parameter.Attribs.HasFlag(GenericParameterAttributes.AllowByRefLike) ||
                parameter.Attribs.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && !IsReferenceType(argument) ||
                parameter.Attribs.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) && !IsNonNullableValueType(argument) ||
                parameter.Attribs.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !HasDefaultConstructor(argument)) return false;

            foreach (var constraint in parameter.Constraints) {
                if (!IsAssignable(argument, constraint.Sig.Type.GetSpec(instantiation))) return false;
            }
        }
        return true;
    }

    private bool HasDefaultConstructor(TypeDesc type)
    {
        if (type is GenericParamType parameter) {
            var flags = Resolve(parameter).Attribs;
            return (flags & (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint)) != 0;
        }
        if (type.IsValueType) return true;
        type = Normalize(type);
        return type is TypeDefOrSpec { IsInterface: false } nominal && !nominal.Attribs.HasFlag(TypeAttributes.Abstract) &&
            nominal.Methods.Any(method => method.IsConstructor && method.IsPublic && method.ParamSig.Count == 1);
    }

    public bool IsAssignable(TypeDesc source, TypeDesc target) => IsAssignable(source, target, new());

    private bool IsAssignable(TypeDesc source, TypeDesc target, HashSet<(TypeDesc, TypeDesc)> visited)
    {
        if (source == target || target == PrimType.Object) return true;
        if (!visited.Add((source, target))) return false;
        if (source is GenericParamType parameter) {
            parameter = Resolve(parameter);
            if (target.IsCorelibType(typeof(ValueType)) && IsNonNullableValueType(parameter)) return true;
            return parameter.Constraints.Any(constraint => IsAssignable(constraint.Sig.Type, target, visited));
        }
        if (target is GenericParamType || target.IsValueType) return false;

        source = Normalize(source);
        // Closed reference types can also use variance. Signature variables
        // require an exact inherited interface/base instantiation to prove it.
        if (!source.IsUnboundGeneric && !target.IsUnboundGeneric && source.IsManagedObject() && source.IsAssignableTo(target)) return true;
        return source.Interfaces.Any(type => IsAssignable(type, target, visited)) ||
            source.BaseType is { } parent && IsAssignable(parent, target, visited);
    }

    public bool CanContainMissing(TypeDesc type) => CanContainMissing(type, new());

    private bool CanContainMissing(TypeDesc type, HashSet<TypeDesc> visited)
    {
        if (IsNonNullableValueType(type)) return false;
        if (type is not GenericParamType parameter) return _resolver.Import(typeof(Missing)).IsAssignableTo(type);
        if (!visited.Add(type)) return true;
        return Resolve(parameter).Constraints.All(constraint => constraint.Sig.Type is GenericParamType other
            ? CanContainMissing(other, visited)
            : constraint.Sig.Type.IsUnboundGeneric || CanContainMissing(constraint.Sig.Type, visited));
    }

    private GenericParamType Resolve(GenericParamType parameter)
        => (GenericParamType)_context.GetArgument(parameter.Index, parameter.IsMethodParam)!;

    private TypeDesc Normalize(TypeDesc type) => type is PrimType primitive ? primitive.GetDefinition(_resolver) : type;
}
