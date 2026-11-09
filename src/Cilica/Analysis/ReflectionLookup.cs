namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Resolves unambiguous, constant reflection lookups against assembly metadata. </summary>
internal static class ReflectionLookup
{
    const BindingFlags SupportedFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public sealed record Result(MemberDesc Member, TypeDefOrSpec ReflectedType);

    public static Result? Resolve(CallInst lookup)
    {
        if (!lookup.Method.DeclaringType.IsCorelibType(typeof(Type)) || lookup.IsStatic ||
            lookup.Method.Name is not ("GetField" or "GetMethod" or "GetProperty") ||
            lookup.Args.Length is not (2 or 3) || lookup.Args[1] is not ConstString name ||
            TypeUtils.GetReflectedType(lookup.Args[0]) is not TypeDefOrSpec type ||
            type.IsUnboundGeneric || type.IsInterface || type.Attribs.HasFlag(TypeAttributes.Import)) return null;

        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (lookup.Args.Length == 3) {
            if (!lookup.Method.ParamSig[2].Type.IsCorelibType(typeof(BindingFlags)) ||
                lookup.Args[2] is not ConstInt bits) return null;
            flags = (BindingFlags)bits.Value;
        }
        if ((flags & ~SupportedFlags) != 0) return null;

        // Inherited and hidden members require reflection's member-kind-specific
        // selection rules. A declared member is sufficient only without a name collision.
        if (!flags.HasFlag(BindingFlags.DeclaredOnly)) {
            for (var parent = type.BaseType; parent != null; parent = parent.BaseType) {
                if (Members(parent, lookup.Method.Name).Any(member => member.Name == name.Value)) return null;
            }
        }

        MemberDesc? selected = null;
        foreach (var member in Members(type, lookup.Method.Name)) {
            if (member.Name != name.Value || !Matches(member, flags)) continue;
            if (selected != null) return null;
            selected = member;
        }
        return selected == null ? null : new(selected, type);
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
