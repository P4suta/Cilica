namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.Util;

public class BasicBlockTests
{
    [Fact]
    public void EdgeEditsMaintainPhiInputsAndRejectIncompleteEdits()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var entry = method.CreateBlock();
        var oldTarget = method.CreateBlock();
        var target = method.CreateBlock();
        entry.SetBranch(oldTarget);
        oldTarget.SetBranch(target);
        var phi = target.InsertPhi(new PhiInst(PrimType.Int32, (oldTarget, ConstInt.CreateI(1))));
        target.SetBranch(new ReturnInst(phi));

        Assert.Throws<InvalidOperationException>(() => entry.RedirectEdge(oldTarget, target));
        Assert.Same(oldTarget, ((BranchInst)entry.Last).Then);
        Assert.Equal(1, target.NumPreds);

        entry.RedirectEdge(oldTarget, target, _ => ConstInt.CreateI(2));

        Assert.Equal(2, phi.NumArgs);
        Assert.Equal(ConstInt.CreateI(2), phi.GetValue(entry));
        Assert.Empty(Cilica.IR.Utils.IRVerifier.Diagnose(method));
    }

    [Fact]
    public void InsertBeforeTerminatorPreservesDefinitionOrder()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32, PrimType.Int32);
        var block = method.CreateBlock();
        var first = new BinaryInst(BinaryOp.Add, method.Args[0], ConstInt.CreateI(1));
        block.InsertAnteLast(first);
        var ret = new ReturnInst(first);
        block.SetBranch(ret);
        var second = new BinaryInst(BinaryOp.Add, first, ConstInt.CreateI(2));
        block.InsertAnteLast(second);

        Assert.Equal(new Instruction[] { first, second, ret }, [.. block]);
        Assert.Empty(Cilica.IR.Utils.IRVerifier.Diagnose(method));
    }

    [Fact]
    public void Test_InstInserts()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block = method.CreateBlock();

        Assert.Null(block.First);
        Assert.Null(block.Last);
        Assert.False(block.GetEnumerator().MoveNext()); // empty
        Assert.Empty(block.NonPhis()); // empty
        Assert.Empty(block.Phis()); // empty

        var inst1 = new BinaryInst(BinaryOp.Add, ConstInt.CreateI(123), ConstInt.CreateI(456));
        var inst2 = new BinaryInst(BinaryOp.Mul, inst1, ConstInt.CreateI(4));
        var inst3 = new ReturnInst(inst2);
        block.InsertFirst(inst1);
        block.InsertLast(inst2);
        block.InsertLast(inst3);
        Assert.True(block.First == inst1 && block.Last == inst3);

        Assert.True(inst1.Prev == null && inst1.Next == inst2);
        Assert.True(inst2.Prev == inst1 && inst2.Next == inst3);
        Assert.True(inst3.Prev == inst2 && inst3.Next == null);

        var inst4 = new BinaryInst(BinaryOp.Mul, inst2, inst1);
        block.InsertAfter(inst2, inst4);
        Assert.True(inst4.Prev == inst2 && inst4.Next == inst3);

        var inst5 = new BinaryInst(BinaryOp.Sub, inst2, inst4);
        block.InsertBefore(inst3, inst5);
        Assert.True(inst5.Prev == inst4 && inst5.Next == inst3);

        inst4.Remove();
        Assert.True(inst2.Next == inst5 && inst5.Prev == inst2);
    }

    [Fact]
    public void Test_Enumerate()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block = method.CreateBlock();
        var inst1 = new PhiInst(PrimType.Int32);
        var inst2 = new PhiInst(PrimType.Int32);
        var inst3 = new BinaryInst(BinaryOp.Add, inst1, inst2);
        var inst4 = new BinaryInst(BinaryOp.Mul, inst3, inst2);
        var inst5 = new ReturnInst(inst4);

        block.InsertLast(inst1);
        block.InsertLast(inst2);
        block.InsertLast(inst3);
        block.InsertLast(inst4);
        block.InsertLast(inst5);

        Assert.Equal(inst1, block.First);
        Assert.Equal(inst5, block.Last);
        Assert.Equal(inst3, block.FirstNonHeader);

        Assert.Equal(new Instruction[] { inst1, inst2, inst3, inst4, inst5 }, [..block]);
        Assert.Equal(new Instruction[] { inst3, inst4, inst5 }, [..block.NonPhis()]);
        Assert.Equal(new Instruction[] { inst1, inst2 }, [..block.Phis()]);
    }

    [Fact]
    public void Test_Split()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block1 = method.CreateBlock();
        var block2 = method.CreateBlock();
        var block3 = method.CreateBlock();
        var block4 = method.CreateBlock();
        var inst1 = new PhiInst(PrimType.Int32);
        var inst2 = new PhiInst(PrimType.Int32);
        var inst3 = new CompareInst(CompareOp.Slt, inst1, inst2);
        var inst4 = new BranchInst(inst3, block2, block3);
        block1.InsertLast(inst1);
        block1.InsertLast(inst2);
        block1.InsertLast(inst3);
        block1.InsertLast(inst4);

        var inst5 = new BinaryInst(BinaryOp.Mul, inst1, inst2);
        var inst6 = new BranchInst(block4);
        block2.InsertLast(inst5);
        block2.InsertLast(inst6);

        var inst7 = new BinaryInst(BinaryOp.Add, inst1, inst2);
        var inst8 = new BranchInst(block4);
        block3.InsertLast(inst7);
        block3.InsertLast(inst8);

        var inst9 = new PhiInst(PrimType.Int32, (block2, inst5), (block3, inst7));
        var instA = new ReturnInst(inst4);
        block4.InsertLast(inst9);
        block4.InsertLast(instA);

        var newBlock = block1.Split(inst4);
        Assert.True(block1.Last is BranchInst br && br.Then == newBlock);

        Assert.Equal(new[] { newBlock }, block1.Succs.ToList());
        Assert.Equal(new[] { newBlock }, block2.Preds.ToList());
        Assert.Equal(new[] { newBlock }, block3.Preds.ToList());

        Assert.Equal(new[] { block2, block3 }, newBlock.Succs.ToList());

        Assert.Equal(inst4, newBlock.First);
        Assert.Equal(inst4, newBlock.Last);
        Assert.Null(inst4.Prev);
    }

    [Fact]
    public void Test_InsertRange()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block = method.CreateBlock();

        var insts = GetDummyInsts(8);
        for (int i = 0; i < 4; i++) {
            block.InsertLast(insts[i]);
        }
        Assert.Equal(insts[0], block.First);
        Assert.Equal(insts[3], block.Last);
        Assert.Null(block.First.Prev);
        Assert.Null(block.Last.Next);

        for (int i = 4; i < 8; i++) {
            block.InsertLast(insts[i]);
        }
        Assert.Equal(insts[0], block.First);
        Assert.Equal(insts[7], block.Last);
        Assert.Null(block.First.Prev);
        Assert.Null(block.Last.Next);
    }

    [Fact]
    public void Test_Remove()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block = method.CreateBlock();

        var insts = GetDummyInsts(8);
        for (int i = 0; i < 8; i++) {
            block.InsertLast(insts[i]);
        }

        insts[3].Remove();
        Assert.Equal(insts.Except(new[] { insts[3] }), [..block]);
    }

    [Fact]
    public void Test_SwitchEdgesAreUnique()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Void);
        var block1 = method.CreateBlock();
        var block2 = method.CreateBlock();
        var block3 = method.CreateBlock();
        var block4 = method.CreateBlock();

        block1.InsertLast(new SwitchInst(ConstInt.CreateI(0), block4, block2, block3, block2, block3));
        block2.SetBranch(block4);
        block3.SetBranch(block4);
        block4.InsertLast(new ReturnInst());

        Assert.Equal(0, block1.NumPreds);
        Assert.Equal(3, block1.NumSuccs);

        Assert.Equal(3, block4.NumPreds);
        Assert.Equal(0, block4.NumSuccs);

        var edges1 = new[] { block2, block3, block4 }.ToHashSet();
        edges1.SymmetricExceptWith(block1.Succs.ToList());
        Assert.Empty(edges1);

        var edges2 = new[] { block1, block2, block3 }.ToHashSet();
        edges2.SymmetricExceptWith(block4.Preds.ToList());
        Assert.Empty(edges2);

        Assert.Equal(new[] { block1 }, block2.Preds.ToList());
        Assert.Equal(new[] { block1 }, block3.Preds.ToList());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Test_ResumeSuccessorCount(bool filter, int count)
    {
        var method = Utils.CreateDummyMethodBody();
        var block = method.CreateBlock();
        var targets = Enumerable.Range(0, count).Select(_ => method.CreateBlock()).ToArray();
        block.SetBranch(new ResumeInst(targets, filter ? ConstInt.CreateI(1) : null));
        Assert.Equal(count, block.NumSuccs);
        Assert.Equal(targets, block.Succs.ToList());

        if (count >= 2) {
            block.RedirectSucc(targets[0], targets[1]);
            Assert.Equal(count - 1, block.NumSuccs);
            Assert.Equal(targets.Skip(1), block.Succs.ToList());
            Assert.Equal(0, targets[0].NumPreds);
            Assert.Equal(new[] { block }, targets[1].Preds.ToList());
            var resume = Assert.IsType<ResumeInst>(block.Last);
            Assert.Equal(filter, resume.IsFromFilter);
            if (filter) Assert.Equal(1, Assert.IsType<ConstInt>(resume.FilterResult).Value);
        }
    }

    private List<Instruction> GetDummyInsts(int count)
    {
        var insts = new List<Instruction>();
        for (int i = 0; i < count; i++) {
            insts.Add(new ReturnInst(ConstInt.CreateI(i)));
        }
        return insts;
    }
}
