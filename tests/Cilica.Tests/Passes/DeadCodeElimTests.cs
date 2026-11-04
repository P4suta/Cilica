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
public class DeadCodeElimTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void DeadCodeElimPreservesStoredLocalAddresses()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var block = body.CreateBlock();
        var value = body.CreateVar(PrimType.Int32);
        var reference = body.CreateVar(PrimType.Int32.CreateByref());
        var ib = new IRBuilder(block);
        ib.CreateStore(value, ConstInt.CreateI(5));
        ib.CreateStore(reference, value);
        ib.CreateReturn(ib.CreateLoad(ib.CreateLoad(reference)));
        Run(body, new DeadCodeElim());

        Assert.Equal(5, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void StaticFieldAccessRetainsTypeInitialization()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var initialized = body.Definition.Module.CreateType(null, "Initialized");
        var effect = type.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var value = initialized.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ctor = initialized.CreateMethod(".cctor", PrimType.Void, [], MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var init = new IRBuilder(ctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateFieldLoad(value);
        ib.CreateReturn(ib.CreateFieldLoad(effect));
        Run(body, new DeadCodeElim());

        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void DeadCodeElimPreservesDivergingCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var spin = body.Definition.DeclaringType.CreateMethod("Spin", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        spin.Body = new MethodBody(spin);
        var entry = spin.Body.CreateBlock();
        var loop = spin.Body.CreateBlock();
        entry.SetBranch(loop);
        loop.SetBranch(loop);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(spin);
        builder.CreateReturn(ConstInt.CreateI(42));
        Run(body, new DeadCodeElim());

        Assert.NotNull(call.Block);
    }

    [Fact]
    public void ChangeReportingTracksPhiEliminationAndNoopSimplification()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(exit);
        right.SetBranch(exit);
        var phi = exit.InsertPhi(new PhiInst(PrimType.Int32, (left, ConstInt.CreateI(1)), (right, ConstInt.CreateI(1))));
        exit.SetBranch(new ReturnInst(phi));
        var ctx = new MethodTransformContext(new Compilation(body.Definition.Module, new VoidLogger(), new()), body);

        Assert.Equal(MethodInvalidations.DataFlow, new DeadCodeElim().Run(ctx).Changes);
        Assert.Null(phi.Block);
        Assert.Equal(MethodInvalidations.None, new SimplifyInsts(_modResolver).Run(ctx).Changes);
    }
}
