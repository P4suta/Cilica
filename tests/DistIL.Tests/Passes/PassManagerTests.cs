namespace DistIL.Tests.Passes;

using System.Reflection;
using DistIL.AsmIO;

[Collection("ModuleResolver")]
public class PassManagerTests(ModuleResolverFixture fixture)
{
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
