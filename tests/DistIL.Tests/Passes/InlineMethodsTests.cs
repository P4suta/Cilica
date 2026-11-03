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
public class InlineMethodsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void InliningPreservesCallVirtNullChecks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var callee = type.CreateMethod("Constant", PrimType.Int32, [new ParamDef(type, "this")]);
        callee.Body = new MethodBody(callee);
        callee.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateReturn(ib.CreateCallVirt(callee, ConstNull.Create()));
        Run(body, new InlineMethods(), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        Assert.Throws<NullReferenceException>(() => Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void InliningPreservesStaticTypeInitialization()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var initialized = body.Definition.Module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var cctor = initialized.CreateMethod(".cctor", PrimType.Void, [], MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        cctor.Body = new MethodBody(cctor);
        var init = new IRBuilder(cctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();
        var callee = initialized.CreateMethod("Noop", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        callee.Body.CreateBlock().SetBranch(new ReturnInst());
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateCall(callee);
        ib.CreateReturn(ib.CreateFieldLoad(effect));
        Run(body, new InlineMethods(), new DeadCodeElim());

        Assert.DoesNotContain(body.Instructions().OfType<CallInst>(), c => c.Method == callee);
        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void InliningBoundsMutualRecursionWithProfitableArguments()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var parameters = Enumerable.Range(0, 10).Select(i => new ParamDef(PrimType.Int32, "p" + i)).ToImmutableArray();
        var a = type.CreateMethod("A", PrimType.Int32, parameters, MethodAttributes.Public | MethodAttributes.Static);
        var b = type.CreateMethod("B", PrimType.Int32, parameters, MethodAttributes.Public | MethodAttributes.Static);
        a.Body = new MethodBody(a);
        b.Body = new MethodBody(b);
        var ia = new IRBuilder(a.Body.CreateBlock());
        ia.CreateReturn(ia.CreateCall(b, a.Body.Args.Cast<Value>().ToArray()));
        var ib = new IRBuilder(b.Body.CreateBlock());
        ib.CreateReturn(ib.CreateCall(a, b.Body.Args.Cast<Value>().ToArray()));
        var root = new IRBuilder(body.CreateBlock());
        root.CreateReturn(root.CreateCall(a, Enumerable.Repeat<Value>(ConstInt.CreateI(1), 10).ToArray()));
        Run(body, new InlineMethods(new() { InitialBudget = 1, BudgetFactor = 0 }));

        Assert.Single(body.Instructions().OfType<CallInst>());
        Assert.True(body.Instructions().Count() < 10);
    }
}
