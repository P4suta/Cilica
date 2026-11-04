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
public class AssertionPropTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_AssertionProp() => CheckIR("AssertionProp.ethil", new AssertionProp());

    [Fact]
    public void AssertionPropagationUsesIncomingPhiEdgePolarity()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Object, PrimType.Object);
        var entry = body.CreateBlock();
        var other = body.CreateBlock();
        var merge = body.CreateBlock();
        var exit = body.CreateBlock();
        var first = new IRBuilder(entry);
        first.SetBranch(first.CreateNe(body.Args[0], ConstNull.Create()), other, merge);
        var second = new IRBuilder(other);
        second.SetBranch(second.CreateNe(body.Args[1], ConstNull.Create()), exit, merge);
        exit.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        var ib = new IRBuilder(merge);
        var phi = ib.CreatePhi(PrimType.Object, (entry, body.Args[0]), (other, body.Args[1]));
        ib.CreateReturn(ib.CreateEq(phi, ConstNull.Create()));
        Run(body, new AssertionProp());
        var run = Utils.Compile<Func<object?, object?, bool>>(body);

        Assert.True(run(null, null));
        Assert.True(run(new object(), null));
        Assert.False(run(new object(), new object()));
    }
}
