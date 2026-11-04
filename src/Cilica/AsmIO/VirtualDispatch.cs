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

        var hierarchy = new List<TypeDefOrSpec>();

        for (TypeDefOrSpec? type = actualType; type != null; type = type.BaseType) {
            hierarchy.Add(type);
        }

        hierarchy.Reverse();

        var roots = new Dictionary<MethodDef, MethodDef>();
        var implementations = new Dictionary<MethodDef, MethodDesc>();
        var declarations = new List<MethodDefOrSpec>();
        MethodDesc? interfaceImpl = null;

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

            foreach (var (declaration, definition) in type.Definition.MethodImpls) {
                var boundDeclaration = declaration.GetSpec(context);
                var implementation = definition.GetSpec(context);

                if (SameMethod(boundDeclaration, requested) && requested.DeclaringType.IsInterface) {
                    interfaceImpl = implementation;
                    explicitInterface = true;
                }

                if (boundDeclaration is MethodDefOrSpec target && roots.TryGetValue(target.Definition, out var root)) {
                    implementations[root] = implementation;
                    roots[definition] = root;
                }
            }

            if (requested.DeclaringType.IsInterface && !explicitInterface &&
                type.Interfaces.Any(itf => itf == requested.DeclaringType || itf.Inherits(requested.DeclaringType))) {
                MethodDesc? match = null;

                for (int parentIndex = index; parentIndex >= 0 && match == null; parentIndex--) {
                    var parent = hierarchy[parentIndex];

                    foreach (var definition in parent.Definition.Methods) {
                        if (!definition.IsPublic || definition.IsStatic || definition.Name != method.Name ||
                            definition.GenericParams.Length != method.GenericParams.Count) {
                            continue;
                        }

                        var member = definition.GetSpec(new GenericContext(parent.GenericParams, method.GenericParams));

                        if (SameSignature(member, requested)) {
                            match = member;
                            break;
                        }
                    }
                }

                interfaceImpl = match;
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

    static bool SameMethod(MethodDesc a, MethodDefOrSpec b)
        => a is MethodDefOrSpec definition && definition.Definition == b.Definition &&
           a.DeclaringType == b.DeclaringType && a.GenericParams.SequenceEqual(b.GenericParams);

    static bool SameSignature(MethodDesc a, MethodDesc b)
        => a.Name == b.Name && a.ReturnSig == b.ReturnSig && a.GenericParams.Count == b.GenericParams.Count &&
           a.ParamSig.Skip(1).SequenceEqual(b.ParamSig.Skip(1));
}
