namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;

[Collection("ModuleResolver")]
public class ControlFlowUtilsTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(GuardKind.Finally)]
    [InlineData(GuardKind.Fault)]
    [InlineData(GuardKind.Catch)]
    public void RemovingTheLastProtectedExitClearsResumeEdges(GuardKind kind)
    {
        var action = fixture.Resolver.Import(typeof(Action));
        var exception = fixture.Resolver.Import(typeof(Exception));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, action, exception);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var throwing = body.CreateBlock();
        var leaving = body.CreateBlock();
        var handler = body.CreateBlock();
        var filter = kind == GuardKind.Catch ? body.CreateBlock() : null;
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(kind, handler, kind == GuardKind.Catch ? exception : null, filter));
        guarded.SetBranch(new BranchInst(ConstInt.CreateI(1), throwing, leaving));
        throwing.SetBranch(new ThrowInst(body.Args[1]));
        leaving.SetBranch(new LeaveInst(exit));
        var callback = new IRBuilder(filter ?? handler);
        callback.CreateCallVirt(action.FindMethod("Invoke"), body.Args[0]);
        (filter ?? handler).SetBranch(new ResumeInst([exit], filter != null ? ConstInt.CreateI(1) : null));
        if (filter != null) handler.SetBranch(new ThrowInst());
        exit.SetBranch(new ReturnInst(ConstInt.CreateI(99)));
        IRVerifier.Verify(body);

        Assert.True(ControlFlowUtils.RemoveUnreachableBlocks(body, foldBranches: true));
        IRVerifier.Verify(body);
        var resume = Assert.Single(body.Instructions().OfType<ResumeInst>());
        Assert.Empty(resume.GetExitTargets());
        Assert.Equal(filter != null, resume.IsFromFilter);
        Assert.Null(leaving.Method);
        Assert.Null(exit.Method);
        Assert.False(ControlFlowUtils.RemoveUnreachableBlocks(body, foldBranches: true));
        int calls = 0;
        var error = new InvalidOperationException();
        var run = Utils.Compile<Func<Action, Exception, int>>(body);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => run(() => calls++, error)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NestedResumesRetainOnlyLiveExitsAndPhiInputs()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var outer = body.CreateBlock();
        var inner = body.CreateBlock();
        var live = body.CreateBlock();
        var dead = body.CreateBlock();
        var innerHandler = body.CreateBlock();
        var outerHandler = body.CreateBlock();
        var innerExit = body.CreateBlock();
        var removedExit = body.CreateBlock();
        var bypass = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], outer, bypass));
        outer.InsertLast(new GuardInst(GuardKind.Finally, outerHandler));
        outer.SetBranch(inner);
        inner.InsertLast(new GuardInst(GuardKind.Finally, innerHandler));
        inner.SetBranch(new BranchInst(ConstInt.CreateI(1), live, dead));
        live.SetBranch(new LeaveInst(innerExit));
        dead.SetBranch(new LeaveInst(removedExit));
        innerHandler.SetBranch(new ResumeInst([innerExit, removedExit]));
        innerExit.SetBranch(new LeaveInst(exit));
        removedExit.SetBranch(new LeaveInst(exit));
        outerHandler.SetBranch(new ResumeInst([exit]));
        bypass.SetBranch(exit);
        var result = exit.InsertPhi(new PhiInst(PrimType.Int32,
            (innerExit, ConstInt.CreateI(17)), (removedExit, ConstInt.CreateI(17)),
            (outerHandler, ConstInt.CreateI(17)), (bypass, ConstInt.CreateI(29))));
        exit.SetBranch(new ReturnInst(result));
        IRVerifier.Verify(body);

        Assert.True(ControlFlowUtils.RemoveUnreachableBlocks(body, foldBranches: true));
        IRVerifier.Verify(body);
        Assert.Same(innerExit, Assert.Single(((ResumeInst)innerHandler.Last).GetExitTargets()));
        Assert.Same(exit, Assert.Single(((ResumeInst)outerHandler.Last).GetExitTargets()));
        Assert.Null(removedExit.Method);
        Assert.Equal(3, result.NumArgs);
        Assert.False(ControlFlowUtils.RemoveUnreachableBlocks(body, foldBranches: true));
        var run = Utils.Compile<Func<bool, int>>(body);
        Assert.Equal(17, run(true));
        Assert.Equal(29, run(false));
    }
}
