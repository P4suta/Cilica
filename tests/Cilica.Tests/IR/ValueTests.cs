namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.Util;

public class ValueTests
{
    [Fact]
    public void SemanticEditsAdvanceMethodRevision()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32, PrimType.Int32);
        var block = method.CreateBlock();
        var value = new BinaryInst(BinaryOp.Add, method.Args[0], ConstInt.CreateI(1));
        block.InsertLast(value);
        block.SetBranch(new ReturnInst(value));
        long revision = method.Revision, cfgRevision = method.ControlFlowRevision;
        value.Right = ConstInt.CreateI(2);

        Assert.True(method.Revision > revision);
        Assert.Equal(cfgRevision, method.ControlFlowRevision);

        revision = method.Revision;
        value.Op = BinaryOp.Sub;

        Assert.True(method.Revision > revision);

        revision = method.Revision;
        value.Op = BinaryOp.Sub;

        Assert.Equal(revision, method.Revision);

        value.ReplaceUses(method.Args[0]);

        Assert.True(method.Revision > revision);
    }

    [Fact]
    public void Test_UseList_SingleUser()
    {
        var value = new FakeTrackedValue(123);
        var inst1 = new BinaryInst(BinaryOp.Add, value, value);

        CheckUses(value, (inst1, 0), (inst1, 1));
    }

    [Fact]
    public void Test_UseList_MultipleUsers()
    {
        var value = new FakeTrackedValue(123);
        var inst1 = new BinaryInst(BinaryOp.Add, value, value);
        var inst2 = new BinaryInst(BinaryOp.Sub, value, value);
        var inst3 = new BinaryInst(BinaryOp.Mul, value, value);

        CheckUses(value,
            (inst1, 0), (inst1, 1), 
            (inst2, 0), (inst2, 1),
            (inst3, 0), (inst3, 1)
        );
    }

    [Fact]
    public void Test_UseList_Replace()
    {
        var value1 = new FakeTrackedValue(123);
        var value2 = new FakeTrackedValue(456);
        var value3 = new FakeTrackedValue(789);
        var value4 = ConstInt.CreateI(111);
        var inst1 = new BinaryInst(BinaryOp.Add, value2, value1);
        var inst2 = new BinaryInst(BinaryOp.Mul, value1, value2);

        value2.ReplaceUses(value3);
        CheckUses(value2);
        CheckUses(value3, (inst1, 0), (inst2, 1));
        Assert.Equal(value3, inst1.Left);
        Assert.Equal(value3, inst2.Right);

        // Also check untracked values
        value1.ReplaceUses(value4);
        CheckUses(value1);
        Assert.Equal(value4, inst1.Right);
        Assert.Equal(value4, inst2.Left);
    }

    [Fact]
    public void Test_UseList_ReplaceToUntrackedAndBack()
    {
        var value1 = new FakeTrackedValue(123);
        var value2 = new FakeTrackedValue(456);
        var value3 = ConstInt.CreateI(111);
        var inst1 = new BinaryInst(BinaryOp.Add, value1, value2);
        var inst2 = new BinaryInst(BinaryOp.Add, value2, value2);

        value2.ReplaceUses(value3);
        CheckUses(value2);
        Assert.False(inst1._useDefs[1].Prev.Exists);
        Assert.False(inst1._useDefs[1].Next.Exists);

        inst1.ReplaceOperand(1, value2);
        CheckUses(value2, (inst1, 1));
    }

    [Fact]
    public void Test_UseList_Relocate()
    {
        var value1 = new FakeTrackedValue(123);
        var value2 = new FakeTrackedValue(456);
        var block = new BasicBlock(null!);
        var phi = new PhiInst(PrimType.Int32, (block, value1), (block, value1), (block, value1), (block, value2));
        //                      0      1          2      3        4       5          6      7
        CheckUses(value1, (phi, 1), (phi, 3), (phi, 5));
        CheckUses(value2, (phi, 7));

        phi.RemoveArg(0, false);
        CheckUses(value1, (phi, 1), (phi, 3));
        CheckUses(value2, (phi, 5));

        phi.RemoveArg(1, false);
        CheckUses(value1, (phi, 1));
        CheckUses(value2, (phi, 3));
    }

    [Fact]
    public void Test_PhiReverseLookupIncludesLastArgument()
    {
        var method = Utils.CreateDummyMethodBody();
        var a = method.CreateBlock();
        var b = method.CreateBlock();
        var first = ConstInt.CreateI(10);
        var last = ConstInt.CreateI(20);
        var phi = new PhiInst(PrimType.Int32, (a, first), (b, last));

        Assert.Same(a, phi.GetBlock(first));
        Assert.Same(b, phi.GetBlock(last));
        phi.RemoveArg(0, removeTrivialPhi: false);
        Assert.Same(b, phi.GetBlock(last));
        Assert.Throws<KeyNotFoundException>(() => phi.GetBlock(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Test_ResumeRetargetMaintainsUses(bool filter)
    {
        var method = Utils.CreateDummyMethodBody();
        var a = method.CreateBlock();
        var b = method.CreateBlock();
        var c = method.CreateBlock();
        var condition = new FakeTrackedValue(1);
        var resume = new ResumeInst([a, a], filter ? condition : null);
        int start = filter ? 1 : 0;
        CheckUses(a, (resume, start));

        resume.SetExitTargets(new HashSet<BasicBlock> { b });
        CheckUses(a);
        CheckUses(b, (resume, start));
        resume.SetExitTargets(new HashSet<BasicBlock> { a, b, c });
        Assert.Equal(3, resume.GetExitTargets().Count());
        foreach (var target in new[] { a, b, c }) Assert.Equal(1, target.NumUses);
        resume.SetExitTargets(new HashSet<BasicBlock> { c });
        CheckUses(a);
        CheckUses(b);
        CheckUses(c, (resume, start));
        resume.SetExitTargets(new HashSet<BasicBlock>());
        CheckUses(c);
        if (filter) {
            Assert.Same(condition, resume.FilterResult);
            CheckUses(condition, (resume, 0));
        }
        resume.Remove();
        CheckUses(condition);
    }

    private void CheckUses(TrackedValue value, params (Instruction, int)[] expUses)
    {
        Assert.Equal(expUses.Length, value.NumUses);

        var userSet = value.Users().AsEnumerable().ToHashSet();
        Assert.Equal(expUses.DistinctBy(u => u.Item1).Count(), userSet.Count);
        userSet.SymmetricExceptWith(expUses.Select(u => u.Item1));
        Assert.Empty(userSet);

        var useSet = value.Uses().AsEnumerable().Select(u => (u.Parent, u.OperIndex)).ToHashSet();
        Assert.Equal(expUses.Length, useSet.Count);
        useSet.SymmetricExceptWith(expUses);
        Assert.Empty(useSet);
    }
}
