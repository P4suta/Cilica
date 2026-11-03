namespace DistIL.Tests.Frontend;

using DistIL;
using DistIL.AsmIO;
using DistIL.Frontend;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;

[Collection("ModuleResolver")]
public class ILImporterTests(ModuleResolverFixture fixture)
{
    [Fact]
    public void PinnedLocalsSurviveImportAndSsaPromotion()
    {
        var module = fixture.Resolver.Resolve("TestAsm.IL");
        var method = module.FindType(null, "ImporterCases")!.FindMethod("PinnedVar1");
        var body = ILImporter.ParseCode(((MethodDefOrSpec)method).Definition);
        var pinned = Assert.Single(body.LocalVars().Where(slot => slot.IsPinned));

        Assert.Equal(PrimType.Int32.CreateByref(), pinned.Type);
        Assert.False(body.InitLocals);

        var writer = new StringWriter();
        IRPrinter.ExportPlain(body, writer);

        string expected = File.ReadAllText("Passes/Cases/Importer.ethil");
        var result = FileChecker.Check(expected, writer.ToString(), StringComparison.Ordinal);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Failures[0].Message + "\n" + writer);

        IRVerifier.Verify(body);
        var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
        PassRunner.Run(new SsaPromotion(), new MethodTransformContext(compilation, body));

        Assert.Contains(pinned, body.LocalVars());

        body.Definition.Body = body;
        var run = Utils.Compile<UpdateValue>(body);
        int value = 21;
        run(ref value);

        Assert.Equal(21 ^ 0x5555, value);
    }

    delegate void UpdateValue(ref int value);
}
