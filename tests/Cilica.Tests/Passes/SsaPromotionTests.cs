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
public class SsaPromotionTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void SsaPromotionPreservesValuesObservedByFinally()
    {
        var callback = _modResolver.Import(typeof(Func<int>));
        var action = _modResolver.Import(typeof(Action<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, callback, action);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Finally, handler));
        var builder = new IRBuilder(guarded, InsertionDir.After);
        builder.CreateStore(slot, builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]));
        guarded.SetBranch(new LeaveInst(exit));
        var cleanup = new IRBuilder(handler);
        cleanup.CreateCallVirt(action.FindMethod("Invoke"), body.Args[1], cleanup.CreateLoad(slot));
        handler.SetBranch(new ResumeInst([exit]));
        exit.SetBranch(new ReturnInst());
        IRVerifier.Verify(body);

        Run(body, new SsaPromotion());
        var run = Utils.Compile<Action<Func<int>, Action<int>>>(body);
        int observed = -1;
        Assert.Throws<InvalidOperationException>(() => run(() => throw new InvalidOperationException(), value => observed = value));
        Assert.Equal(0, observed);
        run(() => 42, value => observed = value);
        Assert.Equal(42, observed);
    }

    [Fact]
    public void SsaPromotionPreservesStoresFromFiltersToHandlers()
    {
        var exception = _modResolver.Import(typeof(Exception));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, exception);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var filter = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, exception, filter));
        guarded.SetBranch(new ThrowInst(body.Args[0]));
        new IRBuilder(filter).CreateStore(slot, ConstInt.CreateI(42));
        filter.SetBranch(new ResumeInst([], ConstInt.CreateI(1)));
        var result = new IRBuilder(handler).CreateLoad(slot);
        handler.SetBranch(new LeaveInst(exit));
        exit.SetBranch(new ReturnInst(result));
        IRVerifier.Verify(body);

        Run(body, new SsaPromotion());
        Assert.Equal(42, Utils.Compile<Func<Exception, int>>(body)(new InvalidOperationException()));
    }

    [Fact]
    public void SsaPromotionPreservesStoresBeforeAndAfterExceptions()
    {
        var callback = _modResolver.Import(typeof(Func<int>));
        var action = _modResolver.Import(typeof(Action));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, callback, action);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        var start = new IRBuilder(entry);
        start.CreateStore(slot, ConstInt.CreateI(7));
        start.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(Exception))));
        var builder = new IRBuilder(guarded, InsertionDir.After);
        builder.CreateStore(slot, builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]));
        builder.CreateCallVirt(action.FindMethod("Invoke"), body.Args[1]);
        guarded.SetBranch(new LeaveInst(exit));
        handler.SetBranch(new LeaveInst(exit));
        var finish = new IRBuilder(exit);
        finish.CreateReturn(finish.CreateLoad(slot));
        IRVerifier.Verify(body);

        Run(body, new SsaPromotion());
        var run = Utils.Compile<Func<Func<int>, Action, int>>(body);
        Assert.Equal(7, run(() => throw new InvalidOperationException(), () => { }));
        Assert.Equal(42, run(() => 42, () => throw new InvalidOperationException()));
        Assert.Equal(42, run(() => 42, () => { }));
    }

    [Fact]
    public void SsaPromotionAllowsParentValuesInProtectedRegions()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        var start = new IRBuilder(entry);
        start.CreateStore(slot, body.Args[0]);
        start.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(Exception))));
        var builder = new IRBuilder(guarded, InsertionDir.After);
        var quotient = builder.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(42), builder.CreateLoad(slot)));
        guarded.SetBranch(new LeaveInst(exit));
        var fallback = new IRBuilder(handler);
        var previous = fallback.CreateLoad(slot);
        handler.SetBranch(new LeaveInst(exit));
        var result = exit.InsertPhi(new PhiInst(PrimType.Int32, (guarded, quotient), (handler, previous)));
        exit.SetBranch(new ReturnInst(result));
        IRVerifier.Verify(body);

        Run(body, new SsaPromotion());
        Assert.Empty(body.LocalVars());
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(21, run(2));
        Assert.Equal(0, run(0));
    }

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
