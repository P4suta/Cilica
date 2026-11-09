namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Resolves unambiguous, constant reflection lookups against assembly metadata. </summary>
internal sealed class ReflectionLookup(ReflectionTypeFacts types)
{
    const BindingFlags SupportedFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public sealed record Result(MemberDesc Member, TypeDefOrSpec ReflectedType);

    readonly Dictionary<CallInst, Result?> _members = new();
    readonly Dictionary<CallInst, TypeDesc?> _types = new();
    readonly Dictionary<CallInst, KnownArray> _typeArguments = new();
    readonly List<CallInst> _resolved = new();

    public Result? Resolve(Value value)
    {
        if (value is not CallInst call) return null;
        if (_members.TryGetValue(call, out var result)) return result;
        result = ResolveMember(call);
        _members.Add(call, result);
        if (result != null) _resolved.Add(call);
        return result;
    }

    public TypeDesc? ResolveType(Value value)
    {
        if (value is not CallInst call) return null;
        if (_types.TryGetValue(call, out var type)) return type;
        type = TypeUtils.GetReflectedType(call);
        if (type == null && call.IsVirtual && call.Method.Name == "MakeGenericType" && call.Method.DeclaringType.IsCorelibType(typeof(Type)) &&
            call.Args.Length == 2 && ResolveType(call.Args[0]) is TypeDef { IsGeneric: true } definition &&
            TryReadTypeArguments(call, definition.GenericParams, out var arguments, out var array) &&
            types.SatisfiesConstraints(definition.GenericParams, arguments, new GenericContext(arguments))) {
            type = definition.GetSpec(arguments);
            _typeArguments.Add(call, array);
        }
        _types.Add(call, type);
        if (type != null) _resolved.Add(call);
        return type;
    }

    private Result? ResolveMember(CallInst lookup)
    {
        if (lookup.IsVirtual && lookup.Method.Name == "MakeGenericMethod" && lookup.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) &&
            lookup.Args.Length == 2 && Resolve(lookup.Args[0]) is { Member: MethodDefOrSpec { IsGeneric: true } method } original &&
            method is not MethodSpec { IsBoundGeneric: true } &&
            TryReadTypeArguments(lookup, method.Definition.GenericParams, out var arguments, out var array) &&
            types.SatisfiesConstraints(method.Definition.GenericParams, arguments, new GenericContext(method.DeclaringType.GenericParams, arguments))) {
            _typeArguments.Add(lookup, array);
            return new(method.GetSpec(arguments), original.ReflectedType);
        }

        if (!lookup.Method.DeclaringType.IsCorelibType(typeof(Type)) || lookup.IsStatic ||
            lookup.Method.Name is not ("GetField" or "GetMethod" or "GetProperty") ||
            lookup.Args.Length is not (2 or 3) || lookup.Args[1] is not ConstString name ||
            ResolveType(lookup.Args[0]) is not TypeDefOrSpec type ||
            !ReflectionTypeFacts.IsRuntimeType(type) || type.Attribs.HasFlag(TypeAttributes.Import)) return null;

        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (lookup.Args.Length == 3) {
            if (!lookup.Method.ParamSig[2].Type.IsCorelibType(typeof(BindingFlags)) ||
                lookup.Args[2] is not ConstInt bits) return null;
            flags = (BindingFlags)bits.Value;
        }
        if ((flags & ~SupportedFlags) != 0) return null;

        MemberDesc? selected = null;
        foreach (var member in Members(type, lookup.Method.Name)) {
            if (member.Name != name.Value || !Matches(member, flags)) continue;
            if (selected != null) return null;
            selected = member;
        }

        if (!flags.HasFlag(BindingFlags.DeclaredOnly)) {
            for (var parent = type.BaseType; parent != null; parent = parent.BaseType) {
                foreach (var inherited in Members(parent, lookup.Method.Name).Where(member => member.Name == name.Value)) {
                    // A virtual override denotes the same inherited slot. Other
                    // inherited/hiding cases require separate lookup selection rules.
                    if (selected is MethodDefOrSpec derived && inherited is MethodDefOrSpec baseMethod &&
                        baseMethod.Attribs.HasFlag(MethodAttributes.Virtual) &&
                        VirtualDispatch.SameMethod(VirtualDispatch.Resolve(baseMethod, type), derived)) continue;
                    return null;
                }
            }
        }
        return selected == null ? null : new(selected, type);
    }

    private bool TryReadTypeArguments(CallInst call, GenericParamType[] parameters, out ImmutableArray<TypeDesc> arguments, out KnownArray array)
    {
        arguments = default;
        if (!KnownArray.TryRead(call.Args[1], call, parameters.Length, out array, types.Resolver.Import(typeof(Type)), allowNull: false)) return false;
        var result = ImmutableArray.CreateBuilder<TypeDesc>(parameters.Length);
        foreach (var value in array.Values) {
            if (ResolveType(value) is not { } type) return false;
            result.Add(type);
        }
        arguments = result.MoveToImmutable();
        return true;
    }

    public void RemoveUnused()
    {
        for (int i = _resolved.Count - 1; i >= 0; i--) {
            var call = _resolved[i];
            // Earlier inlining/devirtualization may have separated receiver
            // validation from the consuming call. Successful lookups are non-null.
            foreach (var check in call.Users().AsEnumerable().OfType<CilIntrinsic.NullCheck>().ToArray()) check.Remove();
            if (call.NumUses != 0 || call.Method.Name == "GetType" && !types.IsNonNull(call.Args[0])) continue;
            call.Remove();
            if (_typeArguments.TryGetValue(call, out var array)) array.Remove();
        }
    }

    private static IEnumerable<MemberDesc> Members(TypeDefOrSpec type, string operation)
        => operation switch {
            "GetField" => type.Fields,
            "GetMethod" => type.Methods.Where(method => !method.IsConstructor && !method.IsStaticConstructor),
            "GetProperty" => type.Definition.Properties,
            _ => throw new UnreachableException()
        };

    private static bool Matches(MemberDesc member, BindingFlags flags)
    {
        bool isPublic, isStatic;
        switch (member) {
            case FieldDesc field:
                isPublic = (field.Attribs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
                isStatic = field.IsStatic;
                break;
            case MethodDesc method:
                isPublic = method.IsPublic;
                isStatic = method.IsStatic;
                break;
            case PropertyDef property:
                var accessors = new[] { property.Getter, property.Setter }.OfType<MethodDef>().Concat(property.OtherAccessors).ToArray();
                if (accessors.Length == 0) return false;
                isPublic = accessors.Any(accessor => accessor.IsPublic);
                isStatic = accessors.Any(accessor => accessor.IsStatic);
                break;
            default:
                throw new UnreachableException();
        }
        return flags.HasFlag(isPublic ? BindingFlags.Public : BindingFlags.NonPublic) &&
            flags.HasFlag(isStatic ? BindingFlags.Static : BindingFlags.Instance);
    }
}
