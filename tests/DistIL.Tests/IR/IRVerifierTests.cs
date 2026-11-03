namespace DistIL.Tests.IR;

using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;

[Collection("ModuleResolver")]
public class IRVerifierTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnValuesMustMatchTheSignature(bool wrongType)
    {
        var body = Utils.CreateDummyMethodBody(PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(wrongType ? ConstString.Create("wrong") : null));

        Assert.Contains(IRVerifier.Diagnose(body), d => d.Message!.Contains("Return value"));
        Assert.Throws<IRVerificationException>(() => IRVerifier.Verify(body));
    }

    [Fact]
    public void CallsMustMatchParameterTypes()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var abs = fixture.Resolver.Import(typeof(Math)).FindMethod("Abs", new MethodSig(PrimType.Int32, [PrimType.Int32]));
        builder.CreateReturn(builder.CreateCall(abs, ConstString.Create("wrong")));

        Assert.Contains(IRVerifier.Diagnose(body), d => d.Message!.Contains("Argument 0"));
    }

    [Fact]
    public void EmptyAndCyclicInstructionListsProduceDiagnostics()
    {
        var body = Utils.CreateDummyMethodBody();

        Assert.NotEmpty(IRVerifier.Diagnose(body));

        var block = body.CreateBlock();

        Assert.NotEmpty(IRVerifier.Diagnose(body));

        var ret = new ReturnInst();
        block.SetBranch(ret);
        ret._next = ret;

        Assert.Throws<IRVerificationException>(() => IRVerifier.Verify(body));
    }

    [Fact]
    public void ForeignEdgesAndUnattachedUsesAreRejected()
    {
        var body = Utils.CreateDummyMethodBody(PrimType.Int32, PrimType.Int32);
        var block = body.CreateBlock();
        var value = new BinaryInst(BinaryOp.Add, body.Args[0], ConstInt.CreateI(1));
        block.InsertLast(value);
        block.SetBranch(new ReturnInst(value));
        var stray = new BinaryInst(BinaryOp.Add, value, ConstInt.CreateI(2));

        Assert.Contains(IRVerifier.Diagnose(body), d => d.Message!.Contains("use count"));

        stray.Remove();

        Assert.Empty(IRVerifier.Diagnose(body));

        var foreign = Utils.CreateDummyMethodBody().CreateBlock();
        foreign.SetBranch(new ReturnInst());
        block.SetBranch(foreign);

        Assert.Contains(IRVerifier.Diagnose(body), d => d.Message!.Contains("belong to this method"));
    }
}
