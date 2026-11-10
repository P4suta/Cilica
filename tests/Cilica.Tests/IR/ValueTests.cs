namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.Util;

public class ValueTests
{
    [Fact]
    public void OperandViewsAcceptCovariantArrays()
    {
        var a = new FakeTrackedValue(1);
        var b = new FakeTrackedValue(2);
        FakeTrackedValue[] values = [a, b];
        var inst = new GrowingIntrinsic(values);

        Assert.Same(values, inst._operands);
        Assert.Equal(new Value[] { a, b }, inst.Operands.ToArray());
        Assert.Equal(new Value[] { a, b }, inst.Args.ToArray());
        inst.RemoveAt(1);
        CheckUses(b);
        inst.Add(ConstInt.CreateI(3));
        Assert.Equal(2, inst.Operands.Length);
        inst.Add(b);
        Assert.Equal(3, inst.Operands.Length);
        CheckUses(a, (inst, 0));
        CheckUses(b, (inst, 2));
        inst.Remove();
        CheckUses(a);
        CheckUses(b);
    }

    [Fact]
    public void IncrementalOperandGrowthCopiesLinearData()
    {
        const int count = 1025;
        var value = new FakeTrackedValue(1);
        var anchor = new BinaryInst(BinaryOp.Add, value, value);
        var inst = new GrowingIntrinsic();
        long operandCopies = 0, useDefCopies = 0;

        for (int i = 0; i < count; i++) {
            var storage = inst._operands;
            var uses = inst._useDefs;
            inst.Add(value);
            if (inst._operands != storage) operandCopies += i;
            if (inst._useDefs != uses) useDefCopies += i;
            Assert.Equal(i + 1, inst.Operands.Length);
        }

        Assert.InRange(operandCopies, 0L, count * 4L);
        Assert.InRange(useDefCopies, 0L, count * 4L);
        Assert.InRange(inst._operands.Length, count, count * 4);
        Assert.Equal(inst._operands.Length, inst._useDefs.Length);
        CheckUses(value, [(anchor, 0), (anchor, 1), ..Enumerable.Range(0, count).Select(i => ((Instruction)inst, i))]);
        inst.Remove();
        CheckUses(value, (anchor, 0), (anchor, 1));
        anchor.Remove();
        CheckUses(value);
    }

    [Fact]
    public void InvalidOperandGrowthPreservesExistingUses()
    {
        var value = new FakeTrackedValue(1);
        var inst = new GrowingIntrinsic();
        inst.Add(value);
        var storage = inst._operands;
        var uses = inst._useDefs;

        Assert.Throws<ArgumentOutOfRangeException>(() => inst.Grow(-1));
        Assert.Throws<OverflowException>(() => inst.Grow(int.MaxValue));
        Assert.Equal(1, inst.Grow(0));
        Assert.Same(storage, inst._operands);
        Assert.Same(uses, inst._useDefs);
        Assert.Same(value, Assert.Single(inst.Operands.ToArray()));
        CheckUses(value, (inst, 0));
        inst.Remove();
        CheckUses(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void OperandRemovalPreservesCapacityAndUseLinks(int removedIndex)
    {
        var a = new FakeTrackedValue(1);
        var b = new FakeTrackedValue(2);
        var anchor = new BinaryInst(BinaryOp.Add, a, a);
        var inst = new GrowingIntrinsic();
        Value[] values = [a, b, a, b, a];
        foreach (var value in values) inst.Add(value);
        var storage = inst._operands;
        var uses = inst._useDefs;
        var expected = values.Where((_, i) => i != removedIndex).ToList();
        var tailReference = inst.GetOperandRef(values.Length - 1);

        inst.RemoveAt(removedIndex);

        Assert.Same(storage, inst._operands);
        Assert.Same(uses, inst._useDefs);
        Assert.Equal(expected, inst.Operands.ToArray());
        Assert.All(inst._operands.Skip(expected.Count), value => Assert.Null(value));
        Assert.Throws<IndexOutOfRangeException>(() => inst.GetOperandRef(expected.Count));
        Assert.Throws<IndexOutOfRangeException>(() => inst.ReplaceOperand(expected.Count, a));
        Assert.Throws<IndexOutOfRangeException>(() => tailReference.Operand);
        CheckUses(a, [(anchor, 0), (anchor, 1), ..ExpectedUses(inst, expected, a)]);
        CheckUses(b, ExpectedUses(inst, expected, b));

        inst.Add(b);
        inst.Add(a);
        expected.Add(b);
        expected.Add(a);
        Assert.Same(b, tailReference.Operand);
        Assert.Same(storage, inst._operands);
        Assert.Same(uses, inst._useDefs);
        Assert.Equal(expected, inst.Operands.ToArray());
        CheckUses(a, [(anchor, 0), (anchor, 1), ..ExpectedUses(inst, expected, a)]);
        CheckUses(b, ExpectedUses(inst, expected, b));

        inst.Clear();
        Assert.Empty(inst.Operands.ToArray());
        Assert.All(inst._operands, value => Assert.Null(value));
        inst.Remove();
        CheckUses(a, (anchor, 0), (anchor, 1));
        CheckUses(b);
        anchor.Remove();
        CheckUses(a);
    }

    [Fact]
    public void IntrinsicCloningExcludesSpareOperandCapacity()
    {
        var a = new FakeTrackedValue(1);
        var b = new FakeTrackedValue(2);
        var c = new FakeTrackedValue(3);
        var inst = new GrowingIntrinsic();
        foreach (var value in new[] { a, b, c }) inst.Add(value);
        Assert.DoesNotContain("null", inst.ToString());
        var copy = (GrowingIntrinsic)inst.CloneWith(PrimType.Int32, [], [c, b, a]);
        var reference = copy.GetOperandRef(2);

        Assert.Equal(new Value[] { a, b, c }, inst.Args.ToArray());
        Assert.Equal(new Value[] { c, b, a }, copy.Args.ToArray());
        copy.Add(a);
        Assert.Same(a, reference.Operand);
        Assert.Equal(new Value[] { c, b, a, a }, copy.Args.ToArray());
        Assert.Equal(3, inst.Args.Length);
        copy.ReplaceOperand(a, b);
        CheckUses(a, (inst, 0));
        CheckUses(b, (inst, 1), (copy, 1), (copy, 2), (copy, 3));
        CheckUses(c, (inst, 2), (copy, 0));
        copy.Remove();
        inst.Remove();
        CheckUses(a);
        CheckUses(b);
        CheckUses(c);
    }

    [Fact]
    public void PhiGrowthWithinCapacityAdvancesMethodRevision()
    {
        var method = Utils.CreateDummyMethodBody();
        var block = method.CreateBlock();
        var first = method.CreateBlock();
        var second = method.CreateBlock();
        var phi = block.InsertPhi(PrimType.Int32);
        phi.AddArg(first, ConstInt.CreateI(1));
        var storage = phi._operands;
        long revision = method.Revision;

        phi.AddArg(second, ConstInt.CreateI(2));

        Assert.Same(storage, phi._operands);
        Assert.True(method.Revision > revision);
        Assert.Equal(2, phi.NumArgs);
        phi.AddArg(method.CreateBlock(), ConstInt.CreateI(3));
        Assert.Equal(3, phi.NumArgs);
        Assert.Throws<IndexOutOfRangeException>(() => phi.GetValue(3));
        Assert.Throws<IndexOutOfRangeException>(() => phi.GetBlock(3));
    }

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
        Assert.Single(resume.GetExitTargets());
        CheckUses(a);
        CheckUses(b);
        CheckUses(c, (resume, start));
        resume.SetExitTargets(new HashSet<BasicBlock>());
        Assert.Empty(resume.GetExitTargets());
        Assert.Equal(start, resume.Operands.Length);
        Assert.Contains("resume", resume.ToString());
        CheckUses(c);
        if (filter) {
            Assert.Same(condition, resume.FilterResult);
            CheckUses(condition, (resume, 0));
        }
        resume.Remove();
        CheckUses(condition);
    }

    private static (Instruction, int)[] ExpectedUses(Instruction inst, IReadOnlyList<Value> operands, Value target)
        => operands.Select((value, index) => (value, index))
            .Where(pair => pair.value == target)
            .Select(pair => (inst, pair.index)).ToArray();

    private void CheckUses(TrackedValue value, params (Instruction, int)[] expUses)
    {
        Assert.Equal(expUses.Length, value.NumUses);

        var uses = value.Uses().AsEnumerable().Take(expUses.Length + 1).ToArray();
        Assert.Equal(expUses.Length, uses.Length);

        var userSet = value.Users().AsEnumerable().ToHashSet();
        Assert.Equal(expUses.DistinctBy(u => u.Item1).Count(), userSet.Count);
        userSet.SymmetricExceptWith(expUses.Select(u => u.Item1));
        Assert.Empty(userSet);

        var useSet = uses.Select(u => (u.Parent, u.OperIndex)).ToHashSet();
        Assert.Equal(expUses.Length, useSet.Count);
        useSet.SymmetricExceptWith(expUses);
        Assert.Empty(useSet);
    }

    sealed class GrowingIntrinsic(params Value[] operands) : IntrinsicInst(PrimType.Int32, operands)
    {
        public override string Namespace => "Tests";
        public override string Name => "Operands";

        public void Add(Value value) => ReplaceOperand(GrowOperands(1), value);
        public int Grow(int amount) => GrowOperands(amount);
        public void RemoveAt(int index) => RemoveOperands(index, 1);
        public void Clear() => RemoveOperands(0, Operands.Length);
    }
}
