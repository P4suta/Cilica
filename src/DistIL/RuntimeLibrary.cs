namespace DistIL;

using DistIL.Analysis;
using DistIL.IR.Utils;

/// <summary> Semantic contracts for recognized methods in the resolved runtime library. </summary>
public sealed class RuntimeLibrary(ModuleResolver resolver)
{
    public ModuleResolver Resolver { get; } = resolver;

    /// <summary> Whether the sequence uses mutable indexed storage with library-specific iteration rules. </summary>
    public static bool IsMutableIndexedSequence(TypeDesc type)
    {
        for (var parent = type; parent != null; parent = parent.BaseType) {
            if (parent.IsCorelibType(typeof(List<>))) {
                return true;
            }
        }

        return false;
    }

    /// <summary> Whether source dispatch is known to use ordinary enumeration or fixed-length storage. </summary>
    public static bool HasKnownSequenceDispatch(Value source)
    {
        var type = source is CilIntrinsic.Box box ? box.SourceType : source.ResultType;

        if (type is ArrayType || type.Kind == TypeKind.String || type.IsCorelibType(typeof(ArraySegment<>))) {
            return true;
        }

        if (IsMutableIndexedSequence(type)) {
            return false;
        }

        if (source is CallInst { Method: MethodDefOrSpec method } call) {
            if (method.DeclaringType.Namespace == "System.Linq" && method.DeclaringType.Name == "Enumerable" &&
                method.Module == method.Module.Resolver.Resolve("System.Linq")) {
                return method.Name == "AsEnumerable" ? HasKnownSequenceDispatch(call.Args[0])
                    : method.Name is "Range" or "Where" or "Select" or "SelectMany" or "Skip" or "Take" or "Cast" or "OfType";
            }

            if (method.Definition.HasCustomAttrib("System.Runtime.CompilerServices", "IteratorStateMachineAttribute")) {
                return true;
            }
        }

        return source is NewObjInst or CilIntrinsic.Box ||
            type is TypeDefOrSpec definition && definition.Attribs.HasFlag(System.Reflection.TypeAttributes.Sealed);
    }

    /// <summary> Whether cardinality can be read without invoking a user implementation. </summary>
    public static bool HasKnownCollectionCount(Value source)
        => source.ResultType is ArrayType || source.ResultType.Kind == TypeKind.String ||
           source is NewObjInst allocation && allocation.Constructor.DeclaringType.IsCorelibType(typeof(List<>));

    public static Value EmitCollectionCount(IRBuilder builder, Value source)
    {
        Ensure.That(HasKnownCollectionCount(source));

        return source.ResultType is ArrayType ? builder.CreateConvert(builder.CreateArrayLen(source), PrimType.Int32)
            : builder.CreateCallVirt(source.ResultType.Kind == TypeKind.String ? "get_Length" : "get_Count", source);
    }

    public FunctionEffects? GetKnownEffects(MethodDef method)
    {
        var type = method.DeclaringType;

        if (!type.IsCorelibType()) {
            return null;
        }

        bool Matches(string name, TypeDesc result, bool instance, params TypeSig[] parameters)
            => method.Name == name && new MethodSig(result, parameters, instance).Matches(method);

        if (type.Namespace == "System.Collections.Generic" && type.Name is "Dictionary`2" or "List`1" or "HashSet`1") {
            if (Matches("get_Count", PrimType.Int32, true)) {
                return new(MemoryEffects.Read, FunctionTraits.MayThrow);
            }

            if (type.Name == "List`1" && Matches("get_Item", GenericParamType.GetUnboundT(0), true, PrimType.Int32)) {
                return new(MemoryEffects.Read, FunctionTraits.MayThrow);
            }

            return null;
        }

        if (type.Namespace == "System" && type.Name == "String") {
            if (Matches("op_Equality", PrimType.Bool, false, PrimType.String, PrimType.String) ||
                Matches("op_Inequality", PrimType.Bool, false, PrimType.String, PrimType.String)) {
                return new(MemoryEffects.None, FunctionTraits.None);
            }

            if (Matches("get_Length", PrimType.Int32, true)) {
                return new(MemoryEffects.None, FunctionTraits.MayThrow);
            }

            if (Matches("ToUpperInvariant", PrimType.String, true) || Matches("ToLowerInvariant", PrimType.String, true)) {
                return new(MemoryEffects.None, FunctionTraits.MayThrow | FunctionTraits.Allocates);
            }
        }

        if (type.Namespace == "System" && type.Name is "Math" or "MathF" && method.IsStatic) {
            var scalar = type.Name == "MathF" ? PrimType.Single : PrimType.Double;
            int arity = method.Name switch {
                "Floor" or "Ceiling" or "Round" or "Truncate" or "Abs" or "Sqrt" or "Cbrt" or
                "Log" or "Log2" or "Log10" or "Exp" or "Sin" or "Cos" or "Tan" or "Sinh" or "Cosh" or "Tanh" or
                "Asin" or "Acos" or "Atan" or "Asinh" or "Acosh" or "Atanh" or
                "ReciprocalEstimate" or "ReciprocalSqrtEstimate" => 1,
                "Pow" or "Atan2" or "Min" or "Max" or "MaxMagnitude" or "MinMagnitude" or "CopySign" => 2,
                "FusedMultiplyAdd" => 3,
                _ => -1
            };

            if (method.GenericParams.Length == 0 && method.ReturnType == scalar && method.ParamSig.Count == arity &&
                method.ParamSig.All(p => p.Type == scalar)) {
                return new(MemoryEffects.None, FunctionTraits.None);
            }
        }

        return null;
    }
}
