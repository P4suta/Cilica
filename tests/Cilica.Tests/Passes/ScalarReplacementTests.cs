namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class ScalarReplacementTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsControlScalarReplacementAndInliningIndependently(bool inline)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var helper = body.Definition.DeclaringType.CreateMethod("Increment", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        helper.Body = new MethodBody(helper);
        var builder = new IRBuilder(helper.Body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(helper.Body.Args[0], ConstInt.CreateI(1)));

        var holder = body.Definition.Module.CreateType(null, "Holder");
        var field = holder.CreateField("Value", PrimType.Int32);
        var ctor = holder.CreateMethod(".ctor", PrimType.Void,
            [new ParamDef(holder, "this"), new ParamDef(PrimType.Int32, "value")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        var il = new ILAssembler();
        il.Emit(ILCode.Ldarg_0);
        il.Emit(ILCode.Call, _modResolver.Import(typeof(object)).FindMethod(".ctor"));
        il.Emit(ILCode.Ldarg_0);
        il.Emit(ILCode.Ldarg_1);
        il.Emit(ILCode.Call, helper);
        il.Emit(ILCode.Stfld, field);
        il.Emit(ILCode.Ret);
        ctor.ILBody = il.Assemble(null);

        builder = new IRBuilder(body.CreateBlock());
        var allocation = builder.CreateNewObj(ctor, body.Args[0]);
        builder.CreateReturn(builder.CreateCall(helper, builder.CreateFieldLoad(field, allocation)));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            EnableInlining = inline, VerifyEachPass = true
        });

        var result = CompilationPipeline.CreateDefault(compilation).Run([body.Definition]);

        result.ThrowIfFailed();
        body = body.Definition.Body!;
        Assert.DoesNotContain(body.Instructions(), inst => inst is NewObjInst);
        Assert.Equal(!inline, body.Instructions().OfType<CallInst>().Any(call => call.Method == helper));
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(42, run(40));
        Assert.Equal(-1, run(-3));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ConstructorExpansionHonorsAssemblyPolicyForAvailableBodies(bool foreign, bool imported, bool crossAssembly)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var module = foreign ? _modResolver.Create("Dependency_" + Guid.NewGuid().ToString("N")) : body.Definition.Module;
        var holder = module.CreateType(null, "Holder");
        var field = holder.CreateField("Value", PrimType.Int32);
        var ctor = holder.CreateMethod(".ctor", PrimType.Void, [new ParamDef(holder, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var builder = new IRBuilder(ctor.Body.CreateBlock());
        builder.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), ctor.Body.Args[0]);
        builder.CreateFieldStore(field, ctor.Body.Args[0], ConstInt.CreateI(42));
        builder.CreateReturn();
        ctor.ILBody = ILGenerator.GenerateCode(ctor.Body);
        if (!imported) ctor.Body = null;
        builder = new IRBuilder(body.CreateBlock());
        var allocation = builder.CreateNewObj(ctor);
        builder.CreateReturn(builder.CreateFieldLoad(field, allocation));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            EnableInlining = false, AllowCrossAssemblyIPO = crossAssembly, VerifyEachPass = true
        });

        PassRunner.Run(new ScalarReplacement(), new MethodTransformContext(compilation, body));

        bool replace = !foreign || crossAssembly;
        Assert.Equal(!replace, body.Instructions().Any(inst => inst is NewObjInst));
        if (replace) {
            Assert.Equal(42, Assert.IsType<ConstInt>(Assert.Single(body.Instructions().OfType<ReturnInst>()).Value).Value);
        }
    }

    [Theory]
    [InlineData(128, false, false)]
    [InlineData(128, false, true)]
    [InlineData(512, true, false)]
    [InlineData(512, true, true)]
    public void ConstructorExpansionRespectsTheCallerILSizeBudget(int limit, bool expand, bool inline)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var holder = body.Definition.Module.CreateType(null, "Holder");
        var field = holder.CreateField("Value", PrimType.Int32);
        var read = holder.CreateMethod("Read", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        read.ImplAttribs = MethodImplAttributes.NoInlining;
        read.Body = new MethodBody(read);
        var counter = holder.CreateField("Counter", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var readBuilder = new IRBuilder(read.Body.CreateBlock());
        var counterAddress = readBuilder.CreateFieldAddr(counter);
        readBuilder.CreateStore(counterAddress, readBuilder.CreateAdd(readBuilder.CreateLoad(counterAddress), ConstInt.CreateI(1)));
        readBuilder.CreateReturn(ConstInt.CreateI(42));
        var ctor = holder.CreateMethod(".ctor", PrimType.Void, [new ParamDef(holder, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        ctor.Body = new MethodBody(ctor);
        var builder = new IRBuilder(ctor.Body.CreateBlock());
        builder.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), ctor.Body.Args[0]);
        for (int i = 0; i < 32; i++) {
            builder.CreateStore(builder.CreateFieldAddr(field, ctor.Body.Args[0]), builder.CreateCall(read));
        }
        builder.CreateReturn();
        builder = new IRBuilder(body.CreateBlock());
        var allocation = builder.CreateNewObj(ctor);
        builder.CreateReturn(builder.CreateLoad(builder.CreateFieldAddr(field, allocation)));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            EnableInlining = inline, MaxInlinedMethodILSize = limit, VerifyEachPass = true
        });
        var context = new MethodTransformContext(compilation, body);

        for (int pass = 0; pass < 3; pass++) {
            PassRunner.Run(new ScalarReplacement(), context);
            PassRunner.Run(new SsaPromotion(), context);
            PassRunner.Run(new DeadCodeElim(), context);
            Assert.InRange(ILGenerator.GenerateCode(body).GetCodeSize(), 1, limit);
        }

        Assert.Equal(expand, allocation.Block == null);
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ScalarReplacementHonorsAddressEscapeAndFinalization(bool escape, bool finalizer)
    {
        var body = Utils.CreateTestMethod(_modResolver, escape ? PrimType.Int32.CreateByref() : PrimType.Int32);
        var holder = body.Definition.Module.CreateType(null, "Holder");
        var field = holder.CreateField("Value", PrimType.Int32);
        var ctor = holder.CreateMethod(".ctor", PrimType.Void, [new ParamDef(holder, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var cb = new IRBuilder(ctor.Body.CreateBlock());
        cb.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), ctor.Body.Args[0]);
        cb.CreateReturn();

        if (finalizer) {
            var finalize = holder.CreateMethod("Finalize", PrimType.Void, [new ParamDef(holder, "this")], MethodAttributes.Family | MethodAttributes.Virtual);
            finalize.Body = new MethodBody(finalize);
            finalize.Body.CreateBlock().SetBranch(new ReturnInst());
        }

        var ib = new IRBuilder(body.CreateBlock());
        var allocation = ib.CreateNewObj(ctor);
        var address = ib.CreateFieldAddr(field, allocation);
        ib.CreateStore(address, ConstInt.CreateI(42));
        ib.CreateReturn(escape ? address : ib.CreateLoad(address));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            EnableInlining = false, VerifyEachPass = true
        });
        var context = new MethodTransformContext(compilation, body);
        PassRunner.Run(new ScalarReplacement(), context);
        PassRunner.Run(new SsaPromotion(), context);

        Assert.Equal(escape || finalizer, allocation.Block != null);

        if (!escape && !finalizer) {
            Assert.Equal(42, Utils.Compile<Func<int>>(body)());
        }
    }
}
