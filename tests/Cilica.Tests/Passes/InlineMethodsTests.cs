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
public class InlineMethodsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void PruningConstantBranchesMaintainsFinallyExits(bool takeLeft, bool inline)
    {
        var action = _modResolver.Import(typeof(Action));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, action);
        var callee = body.Definition.DeclaringType.CreateMethod("WithFinally", PrimType.Int32,
            [new ParamDef(PrimType.Bool, "takeLeft"), new ParamDef(action, "cleanup")],
            MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        var entry = callee.Body.CreateBlock();
        var guarded = callee.Body.CreateBlock();
        var left = callee.Body.CreateBlock();
        var right = callee.Body.CreateBlock();
        var handler = callee.Body.CreateBlock();
        var exitLeft = callee.Body.CreateBlock();
        var exitRight = callee.Body.CreateBlock();
        var exit = callee.Body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Finally, handler));
        guarded.SetBranch(new BranchInst(callee.Body.Args[0], left, right));
        left.SetBranch(new LeaveInst(exitLeft));
        right.SetBranch(new LeaveInst(exitRight));
        new IRBuilder(handler).CreateCallVirt(action.FindMethod("Invoke"), callee.Body.Args[1]);
        handler.SetBranch(new ResumeInst([exitLeft, exitRight]));
        exitLeft.SetBranch(exit);
        exitRight.SetBranch(exit);
        var result = exit.InsertPhi(new PhiInst(PrimType.Int32,
            (exitLeft, ConstInt.CreateI(17)), (exitRight, ConstInt.CreateI(29))));
        exit.SetBranch(new ReturnInst(result));
        IRVerifier.Verify(callee.Body);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(callee, ConstInt.CreateI(takeLeft ? 1 : 0), body.Args[0]));

        MethodBody transformed;
        if (inline) {
            Run(body, new InlineMethods());
            Assert.DoesNotContain(body.Instructions().OfType<CallInst>(), call => call.Method == callee);
            transformed = body;
        } else {
            callee.Body.Args[0].ReplaceUses(ConstInt.CreateI(takeLeft ? 1 : 0));
            Run(callee.Body, new DeadCodeElim());
            transformed = callee.Body;
        }
        Assert.Single(Assert.Single(transformed.Instructions().OfType<ResumeInst>()).GetExitTargets());
        var run = Utils.Compile<Func<Action, int>>(body);
        int cleanups = 0;
        Assert.Equal(takeLeft ? 17 : 29, run(() => cleanups++));
        Assert.Equal(1, cleanups);
        var error = new InvalidOperationException();
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => run(() => throw error)));
    }

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
