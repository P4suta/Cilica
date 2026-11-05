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
    [InlineData(128, false)]
    [InlineData(512, true)]
    public void ConstructorExpansionRespectsTheCallerILSizeBudget(int limit, bool expand)
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
            MaxInlinedMethodILSize = limit, VerifyEachPass = true
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
        Run(body, new ScalarReplacement(), new SsaPromotion());

        Assert.Equal(escape || finalizer, allocation.Block != null);

        if (!escape && !finalizer) {
            Assert.Equal(42, Utils.Compile<Func<int>>(body)());
        }
    }
}
