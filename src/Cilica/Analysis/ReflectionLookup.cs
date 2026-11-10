namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Resolves unambiguous, constant reflection lookups against assembly metadata. </summary>
internal sealed class ReflectionLookup(ReflectionTypeFacts types)
{
    const BindingFlags SupportedFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // A null Result is unknown; a null Member is a proven absence.
    public sealed record Result(MemberDesc? Member, TypeDefOrSpec ReflectedType);

    readonly Dictionary<CallInst, Result?> _members = new();
    readonly Dictionary<CallInst, TypeDesc?> _types = new();
    readonly Dictionary<CallInst, KnownArray> _typeArguments = new();
    readonly List<(CallInst Call, bool NonNull)> _resolved = new();
    VirtualDispatch.Cache? _dispatch;

    public MethodDesc? ResolveVirtualMethod(MethodDefOrSpec method, TypeDefOrSpec actualType)
        => (_dispatch ??= new()).Resolve(method, actualType);

    public Result? Resolve(Value value)
    {
        if (value is not CallInst call) return null;
        if (_members.TryGetValue(call, out var result)) return result;
        result = ResolveMember(call);
        _members.Add(call, result);
        if (result != null) _resolved.Add((call, result.Member != null));
        return result;
    }

    public TypeDesc? ResolveType(Value value)
    {
        if (value is not CallInst call) return null;
        if (_types.TryGetValue(call, out var type)) return type;
        type = TypeUtils.GetReflectedType(call) ?? ResolveGenericType(call);
        _types.Add(call, type);
        if (type != null) _resolved.Add((call, true));
        return type;
    }

    private Result? ResolveMember(CallInst lookup)
        => lookup.Method.Name switch {
            "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)) => ResolveAccessor(lookup),
            "MakeGenericMethod"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) => ResolveGenericMethod(lookup),
            "GetField" or "GetMethod" or "GetProperty"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(Type)) => ResolveNamedMember(lookup),
            _ => null
        };

    private TypeDesc? ResolveGenericType(CallInst call)
    {
        if (!call.IsVirtual || call.NumArgs != 2 || call.Method.Name != "MakeGenericType") return null;
        if (!call.Method.DeclaringType.IsCorelibType(typeof(Type))) return null;
        if (ResolveType(call.Args[0]) is not TypeDef { IsGeneric: true } definition) return null;
        if (!TryReadTypeArguments(call, definition.GenericParams, out var arguments, out var array)) return null;
        if (!types.SatisfiesConstraints(definition.GenericParams, arguments, new GenericContext(arguments))) return null;

        _typeArguments.Add(call, array);
        return definition.GetSpec(arguments);
    }

    private Result? ResolveGenericMethod(CallInst lookup)
    {
        if (!lookup.IsVirtual || lookup.NumArgs != 2) return null;
        if (Resolve(lookup.Args[0]) is not { Member: MethodDefOrSpec { IsGeneric: true } method } original) return null;
        if (method is MethodSpec { IsBoundGeneric: true }) return null;
        if (!TryReadTypeArguments(lookup, method.Definition.GenericParams, out var arguments, out var array)) return null;

        var context = new GenericContext(method.DeclaringType.GenericParams, arguments);
        if (!types.SatisfiesConstraints(method.Definition.GenericParams, arguments, context)) return null;
        _typeArguments.Add(lookup, array);
        return new(method.GetSpec(arguments), original.ReflectedType);
    }

    private Result? ResolveNamedMember(CallInst lookup)
    {
        if (lookup.IsStatic || lookup.NumArgs is not (2 or 3)) return null;
        if (lookup.Args[1] is not ConstString name) return null;
        if (ResolveType(lookup.Args[0]) is not TypeDefOrSpec type) return null;
        if (!ReflectionTypeFacts.IsRuntimeType(type) || type.Attribs.HasFlag(TypeAttributes.Import)) return null;
        if (GetBindingFlags(lookup) is not { } flags) return null;

        MemberDesc? selected = null;
        foreach (var member in Members(type, lookup.Method.Name)) {
            if (member.Name != name.Value || !Matches(member, flags)) continue;
            if (selected != null) return null;
            selected = member;
        }

        if (!flags.HasFlag(BindingFlags.DeclaredOnly) && RequiresInheritedSelection(type, lookup.Method.Name, name.Value, selected)) return null;
        return new(selected, type);
    }

    private static BindingFlags? GetBindingFlags(CallInst lookup)
    {
        if (lookup.NumArgs == 2) return BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (!lookup.Method.ParamSig[2].Type.IsCorelibType(typeof(BindingFlags))) return null;
        if (lookup.Args[2] is not ConstInt bits) return null;
        var flags = (BindingFlags)bits.Value;
        return (flags & ~SupportedFlags) == 0 ? flags : null;
    }

    private bool RequiresInheritedSelection(TypeDefOrSpec type, string operation, string name, MemberDesc? selected)
    {
        for (var parent = type.BaseType; parent != null; parent = parent.BaseType) {
            foreach (var inherited in Members(parent, operation).Where(member => member.Name == name)) {
                // A virtual override denotes the same inherited slot. Other
                // inherited/hiding cases require separate lookup selection rules.
                if (selected is MethodDefOrSpec derived && inherited is MethodDefOrSpec baseMethod &&
                    baseMethod.Attribs.HasFlag(MethodAttributes.Virtual) &&
                    VirtualDispatch.SameMethod(ResolveVirtualMethod(baseMethod, type), derived)) continue;
                return true;
            }
        }
        return false;
    }

    private Result? ResolveAccessor(CallInst lookup)
    {
        var methodInfoType = types.Resolver.Import(typeof(MethodInfo));
        bool? nonPublic;
        if (new MethodSig(methodInfoType, [], true).Matches(lookup.Method)) {
            nonPublic = lookup.Method.Name is "get_GetMethod" or "get_SetMethod";
        } else if (lookup.Method.Name is "GetGetMethod" or "GetSetMethod" &&
            new MethodSig(methodInfoType, [PrimType.Bool], true).Matches(lookup.Method)) {
            nonPublic = lookup.Args[1] is ConstInt flag ? flag.Value != 0 : null;
        } else return null;

        if (Resolve(lookup.Args[0]) is not { Member: PropertyDef property, ReflectedType: var owner }) return null;
        var accessor = lookup.Method.Name is "get_GetMethod" or "GetGetMethod" ? property.Getter : property.Setter;
        if (accessor == null) return new(null, owner);
        if (accessor.IsPublic || nonPublic == true) return new(accessor.GetSpec(new GenericContext(owner)), owner);
        return nonPublic == false ? new(null, owner) : null;
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

    public static bool IsMemberLookup(CallInst call)
        => call.Method.Name switch {
            "GetField" or "GetMethod" or "GetProperty" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod" => call.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)),
            "MakeGenericMethod" => call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)),
            _ => false
        };

    public static bool IsTypeLookup(CallInst call)
        => call.Method.Name switch {
            "GetTypeFromHandle" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "GetType" => call.Method.DeclaringType.IsCorelibType(typeof(object)),
            _ => false
        };

    public void ApplyFacts(bool preserveTrimmingDependencies, ICompilationLogger logger)
    {
        // Resolve records dependencies first. Visit consumers first so their
        // removal can expose unused prerequisite lookups in the same traversal.
        for (int i = _resolved.Count - 1; i >= 0; i--) {
            var (call, nonNull) = _resolved[i];
            LogFact(call, nonNull, logger);
            if (nonNull) call.ResultIsNonNull = true;
            else call.ReplaceUses(ConstNull.Create());
            if (preserveTrimmingDependencies) continue;

            // Earlier inlining/devirtualization may have separated receiver
            // validation from the consuming call. Successful lookups are non-null.
            if (nonNull) {
                foreach (var check in call.Users().AsEnumerable().OfType<CilIntrinsic.NullCheck>().ToArray()) {
                    check.Remove();
                }
            }
            if (call.NumUses != 0) continue;
            // Object.GetType still validates its receiver even if its result is known.
            if (call.Method.Name == "GetType" && !types.IsNonNull(call.Args[0])) continue;
            call.Remove();
            if (_typeArguments.TryGetValue(call, out var array)) array.Remove();
        }
    }

    private void LogFact(CallInst call, bool nonNull, ICompilationLogger logger)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        if (nonNull && !call.ResultIsNonNull) {
            EntityDesc? target = _types.TryGetValue(call, out var type) ? type : _members[call]!.Member;
            logger.Trace($"Proved reflection lookup '{call}' in '{call.Block}' non-null for '{target}'.");
        } else if (!nonNull && call.NumUses != 0) {
            logger.Trace($"Folded reflection lookup '{call}' in '{call.Block}' to null (no matching member in '{_members[call]!.ReflectedType}').");
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
