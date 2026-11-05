namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.Analysis;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.CodeGen.Cil;

[Collection("ModuleResolver")]
public class PassManagerTests(ModuleResolverFixture fixture)
{
    [Fact]
    public void OverBudgetMethodsKeepTheirExecutableInput()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var original = body.Definition.ILBody = ILGenerator.GenerateCode(body);
        body.Definition.Body = null;
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { MaxMethodInstructions = 1 });

        var result = CompilationPipeline.CreateDefault(compilation).Run([body.Definition]);
        Assert.Equal(MethodCompilationStatus.Unsupported, Assert.Single(result.Methods).Status);
        Assert.Same(original, body.Definition.ILBody);
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void RuntimeAsyncBodiesAreRetainedWithoutImportingTheirLogicalReturns()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, fixture.Resolver.Import(typeof(Task)));
        var method = body.Definition;
        method.ImplAttribs |= (MethodImplAttributes)0x2000;
        var assembler = new ILAssembler();
        assembler.Emit(ILCode.Ret);
        var original = method.ILBody = assembler.Assemble(null);
        method.Body = null;
        var compilation = new Compilation(method.Module, new VoidLogger(), new());

        var result = CompilationPipeline.CreateDefault(compilation).Run([method]);
        Assert.True(result.Succeeded);
        Assert.Equal(MethodCompilationStatus.Unchanged, Assert.Single(result.Methods).Status);
        Assert.Same(original, method.ILBody);
        Assert.Null(method.Body);
        Assert.Equal(InlineRejectReason.Ineligible, compilation.GetAnalysis<InliningAdvisor>().EarlyCheck(method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPassOutputRollsBackAndNotifiesInspectors(bool unsupported)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var originalIL = body.Definition.ILBody = ILGenerator.GenerateCode(body);
        int types = body.Definition.Module.TypeDefs.Count;
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });

        Assert.Equal(types, compilation.Module.TypeDefs.Count);

        var observer = new Observer();
        var manager = new PassManager() { Compilation = compilation, Inspectors = { observer } };
        manager.AddPasses().Apply(new InvalidReturnPass(unsupported));
        var result = manager.Run([body.Definition]);

        Assert.False(result.Succeeded);
        Assert.Equal(unsupported ? MethodCompilationStatus.Unsupported : MethodCompilationStatus.Failed, Assert.Single(result.Methods).Status);
        Assert.Same(body, body.Definition.Body);
        Assert.Same(originalIL, body.Definition.ILBody);
        Assert.Equal(types, compilation.Module.TypeDefs.Count);
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
        Assert.Equal(1, observer.Failures);
        Assert.Equal(0, observer.Successes);
        Assert.True(observer.Finished);
        Assert.Null(compilation.GetAnalysis<InliningAdvisor>().GetMethodBodyForIPO(body.Definition));
        Assert.Throws<CompilationException>(result.ThrowIfFailed);
    }

    sealed class InvalidReturnPass(bool unsupported) : IMethodPass
    {
        public MethodPassResult Run(MethodTransformContext context)
        {
            context.Compilation.CreateStaticRva(new byte[32]);
            context.Method.EntryBlock.SetBranch(new ReturnInst());

            if (unsupported) {
                throw new NotSupportedException("Unsupported test instruction");
            }

            return MethodInvalidations.None;
        }
    }

    sealed class Observer : IPassInspector
    {
        public int Failures, Successes;
        public bool Finished;

        public void OnAfterPass(IMethodPass pass, MethodTransformContext context, MethodPassResult result) => Successes++;

        public void OnPassFailed(IMethodPass pass, MethodTransformContext context, Exception error) => Failures++;

        public void OnFinish(Compilation compilation) => Finished = true;
    }

    [Fact]
    public void FailedIpoNormalizationDoesNotPublishABody()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        var method = body.Definition;
        method.Body = null;
        method.ILBody = new ILMethodBody() {
            Instructions = new ILInstruction[] {
            new(ILCode.Ldstr, "wrong"), new(ILCode.Ret)
        }
        };
        var compilation = new Compilation(method.Module, new VoidLogger(), new() { VerifyEachPass = true });

        Assert.Null(compilation.GetAnalysis<InliningAdvisor>().GetMethodBodyForIPO(method));
        Assert.Null(method.Body);
        Assert.True(compilation.HasFailed(method));
    }

    [Fact]
    public void TimingTracksInvocationsChangesFailuresAndAnalysisReuse()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var context = new MethodTransformContext(new Compilation(body.Definition.Module, new VoidLogger(), new()), body);
        var timing = new PassTimingInspector(logStatsOnFinish: false);
        var pass = new QueryAnalysisPass();
        PassRunner.Run(pass, context, [timing]);
        PassRunner.Run(pass, context, [timing]);
        var stats = timing.Stats[typeof(QueryAnalysisPass)];

        Assert.Equal(2, stats.Invocations);
        Assert.Equal(0, stats.ChangedInvocations);
        Assert.Equal(1, stats.AnalysisBuilds);
        Assert.Equal(3, stats.AnalysisCacheHits);
        Assert.True(stats.AllocatedBytes > 0);
        Assert.Throws<NotSupportedException>(() => PassRunner.Run(new InvalidReturnPass(true), context, [timing]));
        Assert.Equal(1, timing.Stats[typeof(InvalidReturnPass)].Failures);
    }

    sealed class QueryAnalysisPass : IMethodPass
    {
        public MethodPassResult Run(MethodTransformContext context)
        {
            context.GetAnalysis<DominatorTree>();
            context.GetAnalysis<DominatorTree>();

            return MethodInvalidations.None;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallerSensitiveCandidateFiltersAreIndependentOfDeclarationOrder(bool helperFirst)
    {
        var module = fixture.Resolver.Create("Candidates_" + Guid.NewGuid().ToString("N"));
        var type = module.CreateType(null, "Methods");

        MethodDef Make(string name) => type.CreateMethod(name, PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);

        var first = Make(helperFirst ? "Helper" : "Root");
        var second = Make(helperFirst ? "Root" : "Helper");
        var helper = helperFirst ? first : second;
        var root = helperFirst ? second : first;
        helper.ILBody = new ILMethodBody() { Instructions = new ILInstruction[] { new(ILCode.Ldc_I4_1), new(ILCode.Ret) } };
        root.ILBody = new ILMethodBody() { Instructions = new ILInstruction[] { new(ILCode.Call, helper), new(ILCode.Ret) } };
        var candidates = PassManager.GetCandidateMethodsFromIL(module, (caller, method) => method == root || caller == root);

        Assert.Equal(new[] { helper, root }, candidates);
    }
}
