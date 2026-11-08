namespace Cilica;

using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.Analysis;
using Cilica.IR.Utils;

public partial class Compilation
{
    const string TypeInitializationHelperName = "<>_Cilica_InitializeType";
    const string ModuleInitializationHelperName = "<>_Cilica_InitializeModule";

    readonly Dictionary<TypeDef, MethodDef> _typeInitializationHelpers = new();
    readonly HashSet<MethodDef> _generatedInitializationHelpers = new();
    MethodDef? _moduleInitializationHelper;

    internal void ExportModuleInitializationHelper()
    {
        if (Module.RequiresModuleInitialization()) {
            _moduleInitializationHelper ??= GetOrCreateInitializationHelper(GetAuxType(), ModuleInitializationHelperName);
        }
    }

    internal void EmitInlineInitialization(IRBuilder builder, MethodDefOrSpec target)
    {
        var caller = builder.Method.Definition;
        bool initializeType = target.RequiresTypeInitialization() &&
            (target.DeclaringType != caller.DeclaringType.GetUnboundSpec() || !caller.RequiresTypeInitialization());

        if (initializeType && GetTypeInitializationHelper(target.DeclaringType) is { } typeHelper && CanCall(typeHelper)) {
            // Calling the target type's method also activates its defining module.
            builder.Emit(new CilIntrinsic.Initialization(typeHelper));
            return;
        }
        if (initializeType && GetTypeInitializationField(target.DeclaringType, caller) is { } field) {
            // Accessing the target type's static storage activates its module too.
            builder.Emit(new CilIntrinsic.Initialization(field));
            return;
        }

        if (target.Module != caller.Module && target.Module.RequiresModuleInitialization()) {
            var moduleHelper = target.Module.FindType(null, "<>_Cilica_Aux") is { } owner
                ? FindInitializationHelper(owner, ModuleInitializationHelperName) : null;
            if (moduleHelper != null && CanCall(moduleHelper)) {
                builder.Emit(new CilIntrinsic.Initialization(moduleHelper));
            } else {
                builder.CreateModuleInitialization(target.DeclaringType);
            }
        }
        if (initializeType) builder.CreateTypeInitialization(target.DeclaringType);

        bool CanCall(MethodDefOrSpec helper)
            => Module != Resolver.CoreLib || new InliningAccess(caller.DeclaringType).CanUseMember(helper);
    }

    private MethodDefOrSpec? GetTypeInitializationHelper(TypeDefOrSpec type)
    {
        MethodDef? helper;
        if (type.Module == Module) {
            if (!_typeInitializationHelpers.TryGetValue(type.Definition, out helper)) {
                helper = GetOrCreateInitializationHelper(type.Definition, TypeInitializationHelperName);
                _typeInitializationHelpers.Add(type.Definition, helper);
            }
        } else {
            helper = FindInitializationHelper(type.Definition, TypeInitializationHelperName);
        }
        return helper?.GetSpec(new GenericContext(type));
    }

    private static FieldDefOrSpec? GetTypeInitializationField(TypeDefOrSpec type, MethodDef caller)
    {
        var access = new InliningAccess(caller.DeclaringType);
        foreach (var definition in type.Definition.Fields) {
            if (!definition.IsStatic || (definition.Attribs & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA)) != 0 ||
                definition.HasCustomAttrib("System", "ThreadStaticAttribute")) continue;

            var field = (FieldDefOrSpec)definition.GetSpec(new GenericContext(type));
            if (access.CanUseMember(field)) return field;
        }
        return null;
    }

    private MethodDef GetOrCreateInitializationHelper(TypeDef owner, string baseName)
    {
        if (FindInitializationHelper(owner, baseName) is { } existing) return existing;

        string name = baseName;
        for (int suffix = 1; owner.Methods.Any(method => method.Name == name); suffix++) name = baseName + "_" + suffix;
        var helper = owner.CreateMethod(name, PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        helper.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        helper.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(Resolver.Import(typeof(CompilerGeneratedAttribute)).FindMethod(".ctor")));
        helper.ILBody = new ILMethodBody { Instructions = new ILInstruction[] { new(ILCode.Ret) }, MaxStack = 0 };
        _generatedInitializationHelpers.Add(helper);
        return helper;
    }

    private static MethodDef? FindInitializationHelper(TypeDef owner, string baseName)
        => owner.Methods.FirstOrDefault(method => method.Name.StartsWith(baseName, StringComparison.Ordinal) &&
            method.IsStatic && !method.IsGeneric && method.ParamSig.Count == 0 && method.ReturnType == PrimType.Void &&
            method.HasCustomAttrib("System.Runtime.CompilerServices", "CompilerGeneratedAttribute") &&
            method.ILBody?.Instructions is [{ OpCode: ILCode.Ret }]);
}
