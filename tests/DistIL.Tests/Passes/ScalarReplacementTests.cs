namespace DistIL.Tests.Passes;

using System.Reflection;
using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;
using MethodBody = DistIL.IR.MethodBody;

[Collection("ModuleResolver")]
public class ScalarReplacementTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
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
