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
public class SsaPromotionTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void SsaPromotionPreservesPartialStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var block = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        block.InsertLast(new StoreInst(slot, ConstInt.CreateI(0x12345678)));
        block.InsertLast(new StoreInst(slot, ConstInt.CreateI(1), PrimType.Byte));
        var load = new LoadInst(slot);
        block.InsertLast(load);
        block.SetBranch(new ReturnInst(load));
        Run(body, new SsaPromotion());

        Assert.Equal(0x12345601, Utils.Compile<Func<int>>(body)());
    }
}
