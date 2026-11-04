namespace Cilica.Tests.Passes;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

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

    [Theory]
    [InlineData(GuardKind.Catch)]
    [InlineData(GuardKind.Finally)]
    [InlineData(GuardKind.Fault)]
    public void FailedDereferencesDoNotEstablishFactsInHandlersOrContinuations(GuardKind kind)
    {
        var callback = _modResolver.Import(typeof(Action<bool>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.String, callback);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(kind, handler,
            kind == GuardKind.Catch ? _modResolver.Import(typeof(NullReferenceException)) : null));
        var ib = new IRBuilder(guarded, InsertionDir.After);
        ib.CreateCallVirt(_modResolver.Import(typeof(string)).FindMethod("get_Length"), body.Args[0]);
        guarded.SetBranch(new LeaveInst(exit));
        var hb = new IRBuilder(handler);
        hb.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[1], hb.CreateEq(body.Args[0], ConstNull.Create()));
        handler.SetBranch(kind == GuardKind.Catch ? new LeaveInst(exit) : new ResumeInst([exit]));
        var eb = new IRBuilder(exit);
        eb.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[1], eb.CreateEq(body.Args[0], ConstNull.Create()));
        eb.CreateReturn();

        Run(body, new AssertionProp());
        var run = Utils.Compile<Action<string?, Action<bool>>>(body);
        var observations = new List<bool>();
        if (kind == GuardKind.Catch) {
            run(null, observations.Add);
            Assert.Equal(new[] { true, true }, observations);
        } else {
            Assert.Throws<NullReferenceException>(() => run(null, observations.Add));
            Assert.Equal(new[] { true }, observations);
        }
        observations.Clear();
        run("value", observations.Add);
        Assert.Equal(kind == GuardKind.Finally ? new[] { false, false } : new[] { false }, observations);
    }

    [Fact]
    public void HandlerEntryDoesNotImplyTheGuardBlocksBranchCondition()
    {
        var callback = _modResolver.Import(typeof(Action));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, callback, PrimType.Object);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var thenBlock = body.CreateBlock();
        var elseBlock = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(Exception))));
        var ib = new IRBuilder(guarded, InsertionDir.After);
        ib.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]);
        ib.SetBranch(ib.CreateNe(body.Args[1], ConstNull.Create()), thenBlock, elseBlock);
        thenBlock.SetBranch(new LeaveInst(exit));
        elseBlock.SetBranch(new LeaveInst(exit));
        var hb = new IRBuilder(handler);
        var isNull = hb.CreateEq(body.Args[1], ConstNull.Create());
        handler.SetBranch(new LeaveInst(exit));
        var phi = exit.InsertPhi(new PhiInst(PrimType.Bool,
            (thenBlock, ConstInt.CreateI(0)), (elseBlock, ConstInt.CreateI(0)), (handler, isNull)));
        exit.SetBranch(new ReturnInst(phi));

        Run(body, new AssertionProp());
        var run = Utils.Compile<Func<Action?, object?, bool>>(body);
        Assert.False(run(null, new object()));
        Assert.True(run(null, null));
        Assert.False(run(() => { }, null));
    }

    [Fact]
    public void BranchConditionUsesAreSpecializedOnTheirIncomingPhiEdges()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Int32);
        var entry = body.CreateBlock();
        var thenBlock = body.CreateBlock();
        var elseBlock = body.CreateBlock();
        var exit = body.CreateBlock();
        var ib = new IRBuilder(entry);
        var condition = ib.CreateCmp(CompareOp.Sgt, body.Args[0], ConstInt.CreateI(0));
        ib.SetBranch(condition, thenBlock, elseBlock);
        thenBlock.SetBranch(exit);
        elseBlock.SetBranch(exit);
        var phi = exit.InsertPhi(new PhiInst(PrimType.Bool, (thenBlock, condition), (elseBlock, condition)));
        exit.SetBranch(new ReturnInst(phi));

        Run(body, new AssertionProp());
        Assert.Equal(1, Assert.IsType<ConstInt>(phi.GetValue(thenBlock)).Value);
        Assert.Equal(0, Assert.IsType<ConstInt>(phi.GetValue(elseBlock)).Value);
        var run = Utils.Compile<Func<int, bool>>(body);
        Assert.False(run(-1));
        Assert.False(run(0));
        Assert.True(run(1));
    }

    [Fact]
    public void FilterDoesNotInheritFactsFromAFailedDereference()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.String);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var filter = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, filterBlock: filter));
        new IRBuilder(guarded, InsertionDir.After)
            .CreateCallVirt(_modResolver.Import(typeof(string)).FindMethod("get_Length"), body.Args[0]);
        guarded.SetBranch(new LeaveInst(exit));
        var fb = new IRBuilder(filter);
        filter.SetBranch(new ResumeInst([exit], fb.CreateEq(body.Args[0], ConstNull.Create())));
        handler.SetBranch(new LeaveInst(exit));
        var phi = exit.InsertPhi(new PhiInst(PrimType.Bool,
            (guarded, ConstInt.CreateI(0)), (handler, ConstInt.CreateI(1)), (filter, new Undef(PrimType.Bool))));
        exit.SetBranch(new ReturnInst(phi));

        Run(body, new AssertionProp());
        var run = Utils.Compile<Func<string?, bool>>(body);
        Assert.True(run(null));
        Assert.False(run("value"));
    }

    [Fact]
    public void BranchFactsOnBackedgesDoNotEscapeIntoTheLoopHeader()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Int32);
        var entry = body.CreateBlock();
        var header = body.CreateBlock();
        var latch = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(header);
        var counter = header.InsertPhi(PrimType.Int32);
        var ran = header.InsertPhi(PrimType.Bool);
        var hb = new IRBuilder(header, InsertionDir.After);
        var condition = hb.CreateSlt(counter, body.Args[0]);
        hb.SetBranch(condition, latch, exit);
        var lb = new IRBuilder(latch);
        counter.AddArg((entry, ConstInt.CreateI(0)), (latch, lb.CreateAdd(counter, ConstInt.CreateI(1))));
        ran.AddArg((entry, ConstInt.CreateI(0)), (latch, condition));
        latch.SetBranch(header);
        exit.SetBranch(new ReturnInst(ran));

        Run(body, new AssertionProp());
        Assert.Same(condition, ((BranchInst)header.Last).Cond);
        Assert.Equal(1, Assert.IsType<ConstInt>(ran.GetValue(latch)).Value);
        var run = Utils.Compile<Func<int, bool>>(body);
        Assert.False(run(-1));
        Assert.False(run(0));
        Assert.True(run(1));
        Assert.True(run(20));
    }

    [Theory]
    [InlineData(CompareOp.FOlt, CompareOp.FOle)]
    [InlineData(CompareOp.FUlt, CompareOp.FOle)]
    [InlineData(CompareOp.FOlt, CompareOp.FUle)]
    [InlineData(CompareOp.FUlt, CompareOp.FUle)]
    [InlineData(CompareOp.FUne, CompareOp.FOeq)]
    public void FloatingPointImplicationsPreserveUnorderedResults(CompareOp guardOp, CompareOp testedOp)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Double, PrimType.Double);
        var entry = body.CreateBlock();
        var thenBlock = body.CreateBlock();
        var elseBlock = body.CreateBlock();
        var ib = new IRBuilder(entry);
        ib.SetBranch(ib.CreateCmp(guardOp, body.Args[0], body.Args[1]), thenBlock, elseBlock);
        foreach (var block in new[] { thenBlock, elseBlock }) {
            var bb = new IRBuilder(block);
            bb.CreateReturn(bb.CreateCmp(testedOp, body.Args[0], body.Args[1]));
        }
        Run(body, new AssertionProp());
        var run = Utils.Compile<Func<double, double, bool>>(body);
        double[] values = [double.NaN, double.NegativeInfinity, -1.0, -0.0, 0.0, 1.0, double.PositiveInfinity];
        foreach (double left in values) {
            foreach (double right in values) {
                bool expected = testedOp switch {
                    CompareOp.FOle => left <= right,
                    CompareOp.FUle => !(left > right),
                    CompareOp.FOeq => left == right,
                    _ => throw new InvalidOperationException()
                };
                Assert.Equal(expected, run(left, right));
            }
        }
    }
}
