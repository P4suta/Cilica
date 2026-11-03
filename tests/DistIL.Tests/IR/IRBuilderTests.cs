namespace DistIL.Tests.IR;

using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;

public class IRBuilderTests
{
    [Fact]
    public void PositionBeforeHeaderlessOrEmptyBlock()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32, PrimType.Int32);
        var block = method.CreateBlock();
        var builder = new IRBuilder(block, InsertionDir.Before);
        var first = builder.CreateAdd(method.Args[0], ConstInt.CreateI(1));
        var ret = builder.CreateReturn(first);
        builder.SetPosition(block, InsertionDir.Before);
        var inserted = builder.CreateAdd(method.Args[0], ConstInt.CreateI(2));
        Assert.Same(inserted, block.First);
        Assert.Same(first, block.First.Next);
        Assert.Same(ret, block.Last);
    }

    [Fact]
    public void Positioning()
    {
        var method = Utils.CreateDummyMethodBody(PrimType.Int32);
        var block = method.CreateBlock();
        var builder = new IRBuilder(block);

        var r1 = builder.CreatePhi(PrimType.Int32, (block, ConstInt.CreateI(1)));
        var r2 = builder.CreatePhi(PrimType.Int32, (block, ConstInt.CreateI(2)));
        var r3 = (BinaryInst)builder.CreateAdd(r2, r1);
        var r4 = (BinaryInst)builder.CreateMul(r1, r3);
        var r5 = builder.Emit(new ReturnInst(r4));

        Assert.Equal(r1, block.First);
        Assert.Equal(r5, block.Last);

        // Block case 1: start (after phis)
        builder.SetPosition(block, InsertionDir.Before);
        Assert.Equal(builder.CreateAdd(r1, r1), r2.Next);

        // Block case 2: before terminator
        builder.SetPosition(block, InsertionDir.BeforeLast);
        Assert.Equal(builder.CreateAdd(r1, r1), block.Last.Prev);

        // Block case 3: end
        builder.SetPosition(block, InsertionDir.After);
        Assert.Equal(builder.CreateAdd(r1, r1), block.Last);

        // Inst case 1: before (middle)
        builder.SetPosition(r3, InsertionDir.Before);
        Assert.Equal(builder.CreateAdd(r1, r1), r3.Prev);

        // Inst case 2: after (middle)
        builder.SetPosition(r3, InsertionDir.After);
        Assert.Equal(builder.CreateAdd(r1, r1), r3.Next);

        // Inst case 3: before start
        builder.SetPosition(block.First, InsertionDir.Before);
        Assert.Equal(builder.CreateAdd(r1, r1), block.First);

        // Inst case 4: after end
        builder.SetPosition(block.Last, InsertionDir.After);
        Assert.Equal(builder.CreateAdd(r1, r1), block.Last);
    }
}
