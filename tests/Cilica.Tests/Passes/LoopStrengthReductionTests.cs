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
public class LoopStrengthReductionTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_LoopStrengthReduction() => CheckIR("LoopStrengthReduction.ethil", new LoopStrengthReduction());

    [Fact]
    public void LoopStrengthReductionRetainsCovariantArrayStoreChecks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Object.CreateArray(), PrimType.Object);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        loop.Build(h => h.CreateSlt(index, h.CreateConvert(h.CreateArrayLen(body.Args[0]), PrimType.Int32)),
            b => b.CreateArrayStore(body.Args[0], index, body.Args[1]));
        loop.Exit.CreateReturn();
        Run(body, new LoopStrengthReduction());

        Assert.Single(body.Instructions().OfType<ArrayAddrInst>());

        var run = Utils.Compile<Action<object[], object>>(body);

        Assert.Throws<ArrayTypeMismatchException>(() => run(new string[1], new object()));

        object[] ordinary = new object[1];
        var value = new object();
        run(ordinary, value);
        Assert.Same(value, ordinary[0]);
    }
}
