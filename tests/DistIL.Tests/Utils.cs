namespace DistIL.Tests;

using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.IR.Utils.Parser;

class Utils
{
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
            if (method.Body != null) method.ILBody = DistIL.CodeGen.Cil.ILGenerator.GenerateCode(method.Body);
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
