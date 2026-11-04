namespace Cilica.Tests;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.IR.Utils.Parser;

class Utils
{
    public static void WriteMetadataModule(string path, Action<MetadataBuilder> populate)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(Path.GetFileName(path)), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(path)), new Version(1, 0), default, default, 0, 0);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        populate(metadata);

        var image = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder());
        var bytes = new BlobBuilder();
        image.Serialize(bytes);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    public static MethodBody CreateTestMethod(ModuleResolver resolver, TypeDesc retType, params TypeDesc[] paramTypes)
    {
        var module = resolver.Create("Test_" + Guid.NewGuid().ToString("N"));
        var type = module.CreateType(null, "TestMethods");
        var def = type.CreateMethod("Run", retType,
            [..paramTypes.Select((t, i) => new ParamDef(t, "arg" + i))],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
        return def.Body = new MethodBody(def);
    }

    public static TDelegate Compile<TDelegate>(MethodBody body) where TDelegate : Delegate
    {
        var module = body.Definition.Module;
        foreach (var method in module.MethodDefs()) {
            if (method.Body != null) method.ILBody = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(method.Body);
        }
        using var stream = new MemoryStream();
        module.Save(stream, null, module.ModName);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var type = assembly.GetType(body.Definition.DeclaringType.Name)!;
        return type.GetMethod(body.Definition.Name)!.CreateDelegate<TDelegate>();
    }

    public static MethodBody CreateDummyMethodBody(TypeDesc? retType = null, params TypeDesc[] paramTypes)
    {
        return CreateDummyMethodBody(
            retType ?? PrimType.Void,
            paramTypes
                .Select((t, i) => new ParamDef(t, "par" + i))
                .ToImmutableArray());
    }
    public static MethodBody CreateDummyMethodBody(
        TypeDesc retType, ImmutableArray<ParamDef> paramSig, 
        System.Reflection.MethodAttributes attribs = default,
        string? name = null, string? typeName = null)
    {
        var type = new TypeDef(null!, null, typeName + "DummyClass");
        var method = new MethodDef(type, retType, paramSig, name ?? "DummyMethod", attribs);
        return new MethodBody(method);
    }
}

sealed class TemporaryDirectory : IDisposable
{
    readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cilica.Tests")) + System.IO.Path.DirectorySeparatorChar;

    public string Path { get; }

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!resolved.StartsWith(_parent, comparison) || resolved.Length <= _parent.Length) {
            throw new InvalidOperationException("Invalid temporary test directory");
        }

        if (Directory.Exists(resolved)) {
            Directory.Delete(resolved, recursive: true);
        }
    }
}

class FakeTrackedValue : TrackedValue
{
    public int Id;
    public FakeTrackedValue(int id)
    {
        Id = id;
        ResultType = PrimType.Int32;
    }
    public override void Print(PrintContext ctx) => ctx.Print(Id.ToString());
}

class FakeParserContext : ParserContext
{
    public FakeParserContext(string code, ModuleResolver modResolver)
        : base(code, modResolver) { }

    public override MethodBody DeclareMethod(
        TypeDef parentType, string name,
        TypeSig returnSig, ImmutableArray<ParamDef> paramSig,
        GenericParamType[] genParams, System.Reflection.MethodAttributes attribs)
    {
        var body = Utils.CreateDummyMethodBody(returnSig.Type, paramSig, attribs, name, parentType.Name);
        DeclaredMethods.Add(body);
        return body;
    }
}
