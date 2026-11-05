namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;

[Collection("ModuleResolver")]
public class IRVerifierTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(TypeKind.Int32, false)]
    [InlineData(TypeKind.Int32, true)]
    [InlineData(TypeKind.Char, false)]
    [InlineData(TypeKind.Char, true)]
    [InlineData(TypeKind.Bool, false)]
    [InlineData(TypeKind.Bool, true)]
    [InlineData(TypeKind.Double, false)]
    [InlineData(TypeKind.Double, true)]
    public void BoxedPrimitivesImplementTheirRuntimeInterfaces(TypeKind kind, bool nullable)
    {
        TypeDesc source = PrimType.GetFromKind(kind);
        if (nullable) source = fixture.Resolver.Import(typeof(Nullable<>)).GetSpec([source]);
        var target = fixture.Resolver.Import(typeof(IComparable));
        var body = Utils.CreateTestMethod(fixture.Resolver, target, source);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.Emit(new CilIntrinsic.Box(source, body.Args[0])));

        Assert.Empty(IRVerifier.Diagnose(body));
    }

    [Theory]
    [InlineData(typeof(Func<>), false, false)]
    [InlineData(typeof(Func<>), true, true)]
    [InlineData(typeof(IReadOnlyList<>), true, true)]
    [InlineData(typeof(Action<>), true, true)]
    [InlineData(typeof(List<>), true, false)]
    public void GenericVarianceRequiresReferenceTypeConstraints(Type genericType, bool referenceConstraint, bool accepted)
    {
        var constraint = fixture.Resolver.Import(typeof(IDisposable));
        var parameter = new GenericParamType(0, true,
            attribs: referenceConstraint ? System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint : 0,
            constraints: [new GenericParamConstraint(constraint, [])]);
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Void);
        var generic = fixture.Resolver.Import(genericType);
        var source = generic.GetSpec(new GenericContext(typeArgs: [GenericParamType.GetUnboundM(0)]));
        var target = generic.GetSpec(new GenericContext(typeArgs: [constraint]));
        if (genericType == typeof(Action<>)) (source, target) = (target, source);
        var method = body.Definition.DeclaringType.CreateMethod("Convert", target, [new ParamDef(source, "value")],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, genericPars: [parameter]);
        body = new MethodBody(method);
        body.CreateBlock().SetBranch(new ReturnInst(body.Args[0]));

        var diagnostics = IRVerifier.Diagnose(body);
        if (accepted) Assert.Empty(diagnostics);
        else Assert.Contains(diagnostics, d => d.Message!.Contains("Return value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoxedStructInterfacesRespectGenericVariance(bool referenceConstraint)
    {
        var constraint = fixture.Resolver.Import(typeof(IDisposable));
        var parameter = new GenericParamType(0, true,
            attribs: referenceConstraint ? System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint : 0,
            constraints: [new GenericParamConstraint(constraint, [])]);
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Void);
        var source = fixture.Resolver.Import(typeof(System.Collections.Immutable.ImmutableArray<>))
            .GetSpec(new GenericContext(typeArgs: [GenericParamType.GetUnboundM(0)]));
        var target = fixture.Resolver.Import(typeof(IEnumerable<IDisposable>));
        var method = body.Definition.DeclaringType.CreateMethod("Convert", target, [new ParamDef(source, "value")],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, genericPars: [parameter]);
        body = new MethodBody(method);
        var block = body.CreateBlock();
        var box = new CilIntrinsic.Box(source, body.Args[0]);
        block.InsertLast(box);
        block.SetBranch(new ReturnInst(box));

        var diagnostics = IRVerifier.Diagnose(body);
        if (referenceConstraint) Assert.Empty(diagnostics);
        else Assert.Contains(diagnostics, d => d.Message!.Contains("Return value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValuesCanLeaveATryOnlyWhenTheHandlerCannotBypassTheirDefinition(bool handlerRejoins)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Int32);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var success = body.CreateBlock();
        var failure = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, fixture.Resolver.Import(typeof(Exception))));
        var value = new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(42), body.Args[0]);
        guarded.InsertLast(value);
        guarded.SetBranch(new LeaveInst(success));
        handler.SetBranch(new LeaveInst(handlerRejoins ? success : failure));
        success.SetBranch(new ReturnInst(value));
        failure.SetBranch(new ReturnInst(ConstInt.CreateI(-1)));

        var diagnostics = IRVerifier.Diagnose(body);
        if (handlerRejoins) {
            Assert.Contains(diagnostics, d => d.Message!.Contains("child region"));
        } else {
            Assert.Empty(diagnostics);
            var run = Utils.Compile<Func<int, int>>(body);
            Assert.Equal(21, run(2));
            Assert.Equal(-1, run(0));
        }
    }

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BoxedParametersMustHaveTheRequiredConstraint(bool methodParameter, bool constrained)
    {
        var disposable = fixture.Resolver.Import(typeof(IDisposable));
        var parameter = new GenericParamType(0, methodParameter,
            constraints: constrained ? [new GenericParamConstraint(disposable, [])] : []);
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Void);
        var owner = body.Definition.Module.CreateType(null, "GenericOwner", genericParams: methodParameter ? [] : [parameter]);
        var token = GenericParamType.GetUnbound(0, methodParameter);
        var definition = owner.CreateMethod("Get", disposable, [new ParamDef(token, "value")],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            genericPars: methodParameter ? [parameter] : []);
        body = new MethodBody(definition);
        var block = body.CreateBlock();
        var box = new CilIntrinsic.Box(token, body.Args[0]);
        block.InsertLast(box);
        block.SetBranch(new ReturnInst(box));

        var diagnostics = IRVerifier.Diagnose(body);
        if (constrained) Assert.Empty(diagnostics);
        else Assert.Contains(diagnostics, d => d.Message!.Contains("Return value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedInterfacesRequireCompatibleValuesOnEveryPath(bool valid)
    {
        var disposable = fixture.Resolver.Import(typeof(IDisposable));
        var body = Utils.CreateTestMethod(fixture.Resolver, disposable, PrimType.Bool,
            fixture.Resolver.Import(typeof(MemoryStream)), valid ? fixture.Resolver.Import(typeof(StringReader)) : PrimType.String);
        var entry = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        var merge = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(merge);
        right.SetBranch(merge);
        var phi = merge.InsertPhi(new PhiInst(PrimType.Object, (left, body.Args[1]), (right, body.Args[2])));
        merge.SetBranch(new ReturnInst(phi));

        var diagnostics = IRVerifier.Diagnose(body);
        if (valid) Assert.Empty(diagnostics);
        else Assert.Contains(diagnostics, d => d.Message!.Contains("Return value"));
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
