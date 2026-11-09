namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("Field")]
    [InlineData("Property")]
    [InlineData("Invoke")]
    [InlineData("StaticDelegate")]
    [InlineData("InstanceDelegate")]
    [InlineData("OpenInstanceDelegate")]
    [InlineData("StaticFactory")]
    [InlineData("FunctionPointer")]
    [InlineData("PrivateDelegateType")]
    [InlineData("GenericType")]
    public void NonPublicMembersFoldAndExecute(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionNonPublicCases");
            Assert.Equal(42, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions,
                instruction => IsReflectionAccess(instruction.Operand) || IsReflectionFactory(instruction.Operand));
            Assert.Equal(42, test.Emit().Invoke(null, null));
        }
    }

    [Fact]
    public void InlinedLookupsCanExposePrivateDeclaringTypes()
    {
        using var test = new Case("HiddenType", "ReflectionNonPublicCases");
        test.Optimize(defaultPipeline: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    public static class ExternalPrivateTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddOne(int value) => value + 1;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateDelegatesAcrossAssembliesRemainCallable(bool defaultPipeline)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var body = CreatePrivateDelegateCaller(resolver.Create("ReflectionAccess_" + Guid.NewGuid().ToString("N")),
            resolver.Import(typeof(ExternalPrivateTarget)));
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body)(41));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        var manager = defaultPipeline ? CompilationPipeline.CreateDefault(compilation) : new PassManager { Compilation = compilation };
        if (!defaultPipeline) manager.AddPasses().Apply<FoldReflection>();
        Assert.True(manager.Run([body.Definition]).Succeeded);
        Assert.DoesNotContain(body.Definition.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body.Definition.Body!)(41));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreLibKeepsReferencesThatWouldRequireAccessSuppression(bool corelib)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = corelib ? resolver.CoreLib : resolver.Create("ReflectionAccess_" + Guid.NewGuid().ToString("N"));
        var target = module.CreateType(null, "PrivateReflectionTarget", TypeAttributes.Public);
        var add = target.CreateMethod("AddOne", PrimType.Int32, [new ParamDef(PrimType.Int32, "value")],
            MethodAttributes.Private | MethodAttributes.Static);
        add.Body = new MethodBody(add);
        var builder = new IRBuilder(add.Body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(add.Body.Args[0], ConstInt.CreateI(1)));
        var body = CreatePrivateDelegateCaller(module, target);
        var manager = new PassManager { Compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true }) };
        manager.AddPasses().Apply<FoldReflection>();
        Assert.True(manager.ApplyPasses([body.Definition]).Succeeded);
        Assert.Equal(corelib, body.Definition.Body!.Instructions().OfType<CallInst>().Any(call => IsReflectionFactory(call.Method)));
        Assert.DoesNotContain(module.GetCustomAttribs(forAssembly: true), attribute => attribute.Type.Name == "IgnoresAccessChecksToAttribute");
    }

    private static MethodBody CreatePrivateDelegateCaller(ModuleDef module, TypeDefOrSpec target)
    {
        var resolver = module.Resolver;
        var caller = module.CreateType(null, "ReflectionCaller", TypeAttributes.Public)
            .CreateMethod("Run", PrimType.Int32, [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        var body = caller.Body = new MethodBody(caller);
        var builder = new IRBuilder(body.CreateBlock());
        var typeType = resolver.Import(typeof(Type));
        var methodInfo = resolver.Import(typeof(MethodInfo));
        var delegateType = resolver.Import(typeof(Func<int, int>));
        var flagsType = resolver.Import(typeof(BindingFlags));
        var type = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle"), builder.Emit(new CilIntrinsic.LoadHandle(resolver, target)));
        var method = builder.CreateCallVirt(typeType.FindMethod("GetMethod", new MethodSig(methodInfo, [PrimType.String, flagsType], true)),
            type, ConstString.Create("AddOne"), ConstInt.Create(flagsType, (int)(BindingFlags.NonPublic | BindingFlags.Static)));
        var create = methodInfo.Methods.Single(method => method.Name == "CreateDelegate" && method.IsGeneric && method.ParamSig.Count == 1)
            .GetSpec([delegateType]);
        var callback = builder.CreateCallVirt(create, method);
        builder.CreateReturn(builder.CreateCallVirt(delegateType.FindMethod("Invoke"), callback, body.Args[0]));
        return body;
    }
}
