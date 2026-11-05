namespace Cilica.AsmIO;

using System.Reflection;

/// <summary> Resolves class slots and class implementations of interface methods. </summary>
internal static class VirtualDispatch
{
    public static MethodDesc? Resolve(MethodDesc method, TypeDefOrSpec actualType)
    {
        if (method is not MethodDefOrSpec requested || method.IsStatic || !actualType.Inherits(method.DeclaringType)) {
            return null;
        }
        // Open instantiations may collapse distinct interface implementations
        // onto the same runtime slot once their arguments are substituted.
        if (requested.DeclaringType.IsInterface && actualType.IsUnboundGeneric) return null;

        var hierarchy = new List<TypeDefOrSpec>();

        for (TypeDefOrSpec? type = actualType; type != null; type = type.BaseType) {
            hierarchy.Add(type);
        }

        hierarchy.Reverse();
        // Class MethodImpl records can redirect several slots, with different
        // propagation rules for PreserveBaseOverrides. Leave these to the CLR.
        if (!requested.DeclaringType.IsInterface && hierarchy.Any(type => type.Definition.MethodImpls.Keys.Any(
            declaration => !declaration.DeclaringType.IsInterface))) return null;

        if (requested.DeclaringType.IsInterface) {
            var interfaces = hierarchy.SelectMany(ImplementedInterfaces).Distinct().ToArray();
            if (!interfaces.Contains(requested.DeclaringType)) {
                var compatible = interfaces.Where(candidate => candidate.Definition == requested.DeclaringType.Definition &&
                    candidate.IsAssignableTo(requested.DeclaringType)).ToArray();
                if (compatible.Length != 1) return null;
                var target = requested.Definition.GetSpec(new GenericContext(compatible[0].GenericParams, requested.GenericParams));
                return Resolve(target, actualType);
            }
        }

        var roots = new Dictionary<MethodDef, MethodDef>();
        var implementations = new Dictionary<MethodDef, MethodDesc>();
        var declarations = new List<MethodDefOrSpec>();
        MethodDesc? interfaceImpl = null;
        int interfaceOwnerIndex = -1;

        for (int index = 0; index < hierarchy.Count; index++) {
            var type = hierarchy[index];
            var context = new GenericContext(type.GenericParams, method.GenericParams);

            foreach (var definition in type.Definition.Methods) {
                if (definition.Name != method.Name || definition.GenericParams.Length != method.GenericParams.Count ||
                    !definition.Attribs.HasFlag(MethodAttributes.Virtual)) {
                    continue;
                }

                var member = definition.GetSpec(context);
                var overridden = definition.Attribs.HasFlag(MethodAttributes.NewSlot) ? null
                    : declarations.LastOrDefault(candidate => SameSignature(member, candidate));
                var root = overridden == null ? definition : roots[overridden.Definition];
                roots[definition] = root;
                implementations[root] = member;
                declarations.Add(member);
            }

            bool explicitInterface = false;
            int explicitMatches = 0;

            foreach (var (declaration, definition) in type.Definition.MethodImpls) {
                var boundDeclaration = declaration.GetSpec(context);
                var implementation = definition.GetSpec(context);

                if (SameMethod(boundDeclaration, requested) && requested.DeclaringType.IsInterface) {
                    if (++explicitMatches > 1) return null;
                    interfaceImpl = implementation;
                    explicitInterface = true;
                    interfaceOwnerIndex = index;
                }

                if (boundDeclaration is MethodDefOrSpec target && roots.TryGetValue(target.Definition, out var root)) {
                    implementations[root] = implementation;
                    roots[definition] = root;
                }
            }

            if (requested.DeclaringType.IsInterface && !explicitInterface &&
                ImplementedInterfaces(type).Contains(requested.DeclaringType)) {
                MethodDesc? match = null;

                // Preserve inherited explicit maps unless a newer class
                // supplies a replacement. Searching behind their owner can
                // incorrectly resurrect an older public implementation.
                for (int parentIndex = index; parentIndex > interfaceOwnerIndex && match == null; parentIndex--) {
                    var parent = hierarchy[parentIndex];

                    foreach (var definition in parent.Definition.Methods) {
                        if (!definition.IsPublic || definition.IsStatic || definition.Name != method.Name ||
                            definition.GenericParams.Length != method.GenericParams.Count) {
                            continue;
                        }

                        var member = definition.GetSpec(new GenericContext(parent.GenericParams, method.GenericParams));

                        if (SameSignature(member, requested)) {
                            if (match != null) return null;
                            match = member;
                        }
                    }
                }

                interfaceImpl = match ?? interfaceImpl;
                if (interfaceImpl != null) interfaceOwnerIndex = index;
            }
        }

        MethodDesc? result;

        if (requested.DeclaringType.IsInterface) {
            result = interfaceImpl;

            if (result is MethodDefOrSpec impl && roots.TryGetValue(impl.Definition, out var slot)) {
                result = implementations[slot];
            }
        } else {
            result = roots.TryGetValue(requested.Definition, out var slot) ? implementations[slot] : null;
        }

        return result?.Attribs.HasFlag(MethodAttributes.Abstract) == false ? result : null;
    }

    static IEnumerable<TypeDefOrSpec> ImplementedInterfaces(TypeDefOrSpec type)
    {
        var pending = new Stack<TypeDefOrSpec>(type.Interfaces.OfType<TypeDefOrSpec>());
        var visited = new HashSet<TypeDefOrSpec>();
        while (pending.TryPop(out var current)) {
            if (!visited.Add(current)) continue;
            yield return current;
            foreach (var parent in current.Interfaces.OfType<TypeDefOrSpec>()) pending.Push(parent);
        }
    }

    static bool SameMethod(MethodDesc a, MethodDefOrSpec b)
        => a is MethodDefOrSpec definition && definition.Definition == b.Definition &&
           a.DeclaringType == b.DeclaringType && a.GenericParams.SequenceEqual(b.GenericParams);

    static bool SameSignature(MethodDesc a, MethodDesc b)
        => a.Name == b.Name && a.ReturnSig == b.ReturnSig && a.GenericParams.Count == b.GenericParams.Count &&
           a.ParamSig.Skip(1).SequenceEqual(b.ParamSig.Skip(1));
}
