namespace DistIL;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using DistIL.Analysis;
using DistIL.CodeGen.Cil;
using MethodBody = IR.MethodBody;

public class Compilation
{
    public ModuleDef Module { get; }
    public ICompilationLogger Logger { get; }
    public CompilationSettings Settings { get; }

    public RuntimeLibrary Runtime { get; }

    public ModuleResolver Resolver => Module.Resolver;

    private TypeDef? _auxType;
    bool _ownsAuxType;
    readonly HashSet<MemberDesc> _initialAuxMembers = new();

    readonly AnalysisCache<IGlobalAnalysis> _analyses = new();
    readonly HashSet<MethodDef> _failedMethods = new();

    public Compilation(ModuleDef module, ICompilationLogger logger, CompilationSettings settings)
    {
        Module = module;
        Logger = logger;
        Settings = settings;
        Runtime = new RuntimeLibrary(module.Resolver);

    }

    public A GetAnalysis<A>() where A : IGlobalAnalysis
        => (A)_analyses.Get(typeof(A), () => A.Create(this));

    public void InvalidateAnalyses(MethodDef method)
    {
        foreach (var analysis in _analyses.Values) {
            analysis.Invalidate(method);
        }
    }

    public bool HasFailed(MethodDef method) => _failedMethods.Contains(method);

    internal void BeginMethod(MethodDef method) => _failedMethods.Remove(method);

    internal void FailMethod(MethodDef method)
    {
        _failedMethods.Add(method);
        InvalidateAnalyses(method);
    }

    internal void EnsureReferencedMembersAccessible(MethodBody body)
    {
        foreach (var inst in body.Instructions()) {
            MemberDesc? member = inst switch {
                CallInst call => call.Method,
                NewObjInst allocation => allocation.Constructor,
                FuncAddrInst address => address.Method,
                FieldAddrInst field => field.Field,
                FieldExtractInst field => field.Field,
                FieldInsertInst field => field.Field,
                _ => null
            };

            if (member?.DeclaringType is not TypeDefOrSpec type) {
                continue;
            }

            bool publicMember = member switch {
                MethodDesc method => method.IsPublic,
                FieldDesc field => (field.Attribs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public,
                _ => false
            };
            bool publicType = (type.Attribs & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic;

            if ((!publicMember && type != body.Definition.DeclaringType) || (!publicType && type.Module != Module)) {
                EnsureMembersAccessible(type.Module);
            }
        }
    }

    /// <summary> Returns the compiler's auxiliary type for the current module. </summary>
    public TypeDef GetAuxType()
    {
        if (_auxType != null) {
            return _auxType;
        }
        const string name = "<>_DistIL_Aux";
        _auxType = Module.FindType(null, name);

        if (_auxType == null) {
            _ownsAuxType = true;
            _auxType = Module.CreateType(null, name, TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);

            var attribCtor = Module.Resolver.Import(typeof(CompilerGeneratedAttribute)).FindMethod(".ctor");
            var typeAttribs = _auxType.GetCustomAttribs(readOnly: false);
            typeAttribs.Add(new CustomAttrib(attribCtor));
        } else {
            _initialAuxMembers.UnionWith(_auxType.Fields);
            _initialAuxMembers.UnionWith(_auxType.Methods);
        }

        return _auxType;
    }

    internal void RemoveUnusedArtifacts()
    {
        if (_auxType == null) {
            return;
        }

        var live = new HashSet<MemberDesc>();
        var pending = new Queue<MethodDef>(Module.MethodDefs().Where(method => method.DeclaringType != _auxType));

        while (pending.TryDequeue(out var method)) {
            if (method.ILBody is { } il) {
                foreach (var inst in il.Instructions) {
                    Mark(inst.Operand as MemberDesc);
                }
            }

            if (method.Body is { } body)
            foreach (var inst in body.Instructions()) {
                Mark(inst switch {
                    CallInst call => call.Method,
                    NewObjInst allocation => allocation.Constructor,
                    FuncAddrInst address => address.Method,
                    FieldAddrInst field => field.Field,
                    FieldExtractInst field => field.Field,
                    FieldInsertInst field => field.Field,
                    _ => null
                });

                if (inst is IntrinsicInst intrinsic) {
                    foreach (var entity in intrinsic.StaticArgs) {
                        Mark(entity as MemberDesc);
                    }
                }
            }
        }

        _auxType.Fields.RemoveAll(field => !_initialAuxMembers.Contains(field) && !live.Contains(field));
        _auxType.Methods.RemoveAll(method => !_initialAuxMembers.Contains(method) && !live.Contains(method));

        if (_ownsAuxType && _auxType.Fields.Count == 0 && _auxType.Methods.Count == 0) {
            Module.RemoveType(_auxType);
            _auxType = null;
            _ownsAuxType = false;
        }

        void Mark(MemberDesc? member)
        {
            MemberDesc? definition = member switch {
                MethodDefOrSpec method => method.Definition,
                FieldDefOrSpec field => field.Definition,
                _ => null
            };

            if (definition?.DeclaringType != _auxType || !live.Add(definition)) {
                return;
            }

            if (definition is MethodDef helper) {
                pending.Enqueue(helper);
            }
        }
    }

    /// <summary> Copies <paramref name="data"/> into a static field RVA. </summary>
    public FieldDef CreateStaticRva(ReadOnlySpan<byte> data)
    {
        Ensure.That(data.Length < 1024 * 1024 * 4, "Cannot allocate RVA bigger than 4MB");

        string fieldName = "Data_" + Convert.ToBase64String(SHA256.HashData(data)).Replace('+', '_').Replace('/', '_').TrimEnd('=');
        var auxType = GetAuxType();

        if (auxType.FindField(fieldName, throwIfNotFound: false) is FieldDef field) {
            Ensure.That(
                data.SequenceEqual(field.MappedData.AsSpan(0, data.Length)),
                "Congratulations, you just found a hash collision! (or a serious bug)");
            return field;
        }
        var (blockType, alignedSize) = CreateRvaBlock(auxType, data.Length);

        var alignedData = new byte[alignedSize];
        data.CopyTo(alignedData);

        var attrs = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.HasFieldRVA;
        return auxType.CreateField(fieldName, blockType, attrs, mappedData: alignedData);
    }

    private (TypeDesc Type, int Size) CreateRvaBlock(TypeDef parentType, int size)
    {
        if (size <= 8) return (PrimType.UInt64, 8);

        size = (size + 15) & ~15; // align to multiples of 16 bytes (for no particular reason).
        string name = "Block" + size;

        var blockType = parentType.FindNestedType(name);
        Ensure.That(blockType == null || blockType.LayoutSize == size);

        if (blockType == null) {
            var attrs = TypeAttributes.NestedAssembly | TypeAttributes.ExplicitLayout;
            blockType = parentType.CreateNestedType(name, attrs, baseType: Resolver.SysTypes.ValueType);
            blockType.LayoutSize = size;
        }
        return (blockType, size);
    }

    HashSet<string> _assembliesToIgnoreAccessChecksFor = new();

    /// <summary> Adds an IgnoresAccessChecksToAttribute for the given module if there's not already one. </summary>
    /// <remarks> This ensures that <see cref="Module"/> can access any private member declared in the given module.  </remarks>
    public void EnsureMembersAccessible(ModuleDef module)
    {
        string name = module.AsmName.Name!;

        if (_assembliesToIgnoreAccessChecksFor.Add(name)) {
            MarkIgnoreAccessChecksToAssembly(name);
        }
    }

    private MethodDesc? m_IgnoreAccessChecksToAttributeCtor;

    private void MarkIgnoreAccessChecksToAssembly(string assemblyName)
    {
        var asmAttribs = Module.GetCustomAttribs(forAssembly: true);

        if (m_IgnoreAccessChecksToAttributeCtor == null) {
            var attribType = Module.FindType("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute");

            if (attribType == null) {
                attribType = Module.CreateType(
                    "System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute",
                    TypeAttributes.BeforeFieldInit,
                    Module.Resolver.CoreLib.FindType("System", "Attribute")
                );
                var attribCtor = attribType.CreateMethod(
                    ".ctor", PrimType.Void,
                    [new ParamDef(attribType, "this"), new ParamDef(PrimType.String, "assemblyName")],
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
                );
                var assembler = new ILAssembler();
                assembler.Emit(ILCode.Ldarg_0);
                assembler.Emit(ILCode.Call, attribType.BaseType!.FindMethod(".ctor", new MethodSig(PrimType.Void, [])));
                assembler.Emit(ILCode.Ret);
                attribCtor.ILBody = assembler.Assemble(null);

                m_IgnoreAccessChecksToAttributeCtor = attribCtor;
            } else {
                m_IgnoreAccessChecksToAttributeCtor = attribType.FindMethod(".ctor", new MethodSig(PrimType.Void, [PrimType.String]));

                foreach (var attrib in asmAttribs) {
                    if (attrib.Constructor == m_IgnoreAccessChecksToAttributeCtor) {
                        _assembliesToIgnoreAccessChecksFor.Add((string)attrib.Args[0]);

                        if ((string)attrib.Args[0] == assemblyName) {
                            return;
                        }
                    }
                }
            }
        }

        asmAttribs.Add(new CustomAttrib(m_IgnoreAccessChecksToAttributeCtor, [assemblyName]));
    }
}

public class CompilationSettings
{
    public bool VerifyEachPass { get; init; }

    /// <summary> If set to true, the resulting module will have a fixed dependency on a little-endianess. </summary>
    public bool AssumeLittleEndian { get; init; } = true; // TODO: add module cctor check

    /// <summary> Whether to allow optimizations to inline or make assumptions about methods defined in other assemblies. </summary>
    public bool AllowCrossAssemblyIPO { get; init; } = true;
}
