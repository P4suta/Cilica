namespace DistIL.Tests.CodeGen;

using System.Reflection;
using System.Reflection.Metadata;
using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.Frontend;
using DistIL.IR;
using DistIL.IR.Utils;
using MethodBody = DistIL.IR.MethodBody;

[Collection("ModuleResolver")]
public class IndirectCallTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedCallsRoundTripThroughImportAndEmission(bool tail)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Int32);
        var target = body.Definition.DeclaringType.CreateMethod("Target", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        target.Body = new MethodBody(target);
        var targetBuilder = new IRBuilder(target.Body.CreateBlock());
        targetBuilder.CreateReturn(targetBuilder.CreateAdd(target.Body.Args[0], ConstInt.CreateI(1)));
        var builder = new IRBuilder(body.CreateBlock());
        var signature = new FuncPtrType(new MethodSig(PrimType.Int32, [PrimType.Int32], isInstance: false));
        var function = builder.Emit(new FuncAddrInst(target));
        var call = builder.Emit(new CilIntrinsic.CallIndirect(signature, function, [body.Args[0]]) { IsTailCall = tail });
        builder.CreateReturn(call);
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        var imported = ILImporter.ParseCode(body.Definition);
        IRVerifier.Verify(imported);
        var importedCall = Assert.Single(imported.Instructions().OfType<CilIntrinsic.CallIndirect>());
        Assert.Equal(tail, importedCall.IsTailCall);
        Assert.Equal(signature, importedCall.FunctionType);
        body.Definition.Body = imported;
        Assert.Equal(42, Utils.Compile<Func<int, int>>(imported)(41));

        var effects = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<GlobalFunctionEffects>().GetEffects(body.Definition);
        Assert.True(effects.MayDiverge && effects.MayAllocate && effects.MayThrow && effects.MayReadMem && effects.MayWriteMem && effects.HasMemoryBarrier);
    }

    [Fact]
    public void FunctionPointerSignaturesPreserveModifiersAndSpecializeGenerics()
    {
        var modifier = fixture.Resolver.Import(typeof(System.Runtime.CompilerServices.CallConvCdecl));
        var parameter = GenericParamType.GetUnboundM(0);
        var signature = new FuncPtrType(new MethodSig(new TypeSig(parameter, [new TypeModifier(modifier, false)]),
            [new TypeSig(parameter.CreateByref(), [new TypeModifier(modifier, false)])],
            new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.Unmanaged, 0)));
        var specialized = (FuncPtrType)signature.GetSpec(new GenericContext(methodArgs: [PrimType.Int32]));
        Assert.Equal(PrimType.Int32, specialized.Signature.ReturnType.Type);
        Assert.Equal(PrimType.Int32.CreateByref(), specialized.Signature.ParamTypes[0].Type);
        Assert.Equal(signature.Signature.ReturnType.Modifiers, specialized.Signature.ReturnType.Modifiers);
        Assert.Equal(signature.Signature.ParamTypes[0].Modifiers, specialized.Signature.ParamTypes[0].Modifiers);

        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.IntPtr, PrimType.Int32.CreateByref());
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.Emit(new CilIntrinsic.CallIndirect(specialized, body.Args[0], [body.Args[1]])));
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "signature.dll");
        body.Definition.Module.Save(path, false);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var rewritten = resolver.Load(path).MethodDefs().Single().ILBody!;
        var actual = (FuncPtrType)Assert.Single(rewritten.Instructions.Where(i => i.OpCode == ILCode.Calli)).Operand!;
        Assert.Equal(CallConvention.Unmanaged, actual.Signature.CallConv);
        Assert.Equal(specialized.Signature.ReturnType.Modifiers[0].Type.Name, actual.Signature.ReturnType.Modifiers[0].Type.Name);
        Assert.Equal(specialized.Signature.ParamTypes[0].Modifiers[0].Type.Name, actual.Signature.ParamTypes[0].Modifiers[0].Type.Name);
        Assert.Equal(specialized, new FuncPtrType(new MethodSig(specialized.Signature.ReturnType,
            specialized.Signature.ParamTypes.ToArray(), new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.Unmanaged, 0))));
    }
}
