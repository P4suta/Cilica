namespace DistIL.Tests.Passes;

using System.Reflection;
using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;
using MethodBody = DistIL.IR.MethodBody;

public abstract class PassTestBase(ModuleResolverFixture fixture)
{
    protected readonly ModuleResolver _modResolver = fixture.Resolver;

    private ModuleDef TestAssembly => _modResolver.Resolve("TestAsm");

    protected static void Run(MethodBody body, params IMethodPass[] passes)
    {
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var ctx = new MethodTransformContext(comp, body);

        foreach (var pass in passes) {
            PassRunner.Run(pass, ctx);
        }

        Assert.DoesNotContain(IRVerifier.Diagnose(body), d => d.Severity == DiagnosticSeverity.Error);
    }

    protected void CheckIR(string filename, IMethodPass pass)
    {
        CheckDisasm(filename, (comp, body, writer) => {
            PassRunner.Run(pass, new MethodTransformContext(comp, body));
            IRVerifier.Verify(body);

            IRPrinter.ExportPlain(body, writer);
            writer.Write("\n\n");
        });
    }

    protected void CheckCIL(string filename)
    {
        CheckDisasm(filename, (comp, body, writer) => {
            var output = ILGenerator.GenerateCode(body);
            writer.WriteLine($"// {body.Definition}");

            foreach (var instruction in output.Instructions) {
                writer.WriteLine(instruction.ToString());
            }

            writer.WriteLine();
        });
    }

    protected void CheckDisasm(string filename, Action<Compilation, MethodBody, StringWriter> processAndDisasm)
    {
        var selfType = TestAssembly.CreateType("RegressionTests", $"_Test_{Path.GetFileNameWithoutExtension(filename)}");
        string source = File.ReadAllText("Passes/Cases/" + filename);

        string fixedSource = $"import RegressionTests from TestAsm\nimport Self = {selfType.Name}\n\n{source}";
        var ctx = IRParser.Populate(fixedSource, _modResolver);
        ctx.ThrowIfError();

        var decls = ctx.DeclaredMethods.ToDictionary(e => e.Definition.Name);

        var comp = new Compilation(TestAssembly, new VoidLogger(), new CompilationSettings());
        var sw = new StringWriter();

        foreach (var (name, body) in decls) {
            processAndDisasm.Invoke(comp, body, sw);
        }

        var result = FileChecker.Check(source, sw.ToString(), StringComparison.Ordinal);

        if (!result.IsSuccess) {
            Directory.CreateDirectory("regress_fail");
            File.WriteAllText($"regress_fail/{filename.Replace('/', '_')}.txt", sw.ToString());

            Assert.True(result.IsSuccess, result.Failures[0].Message);
        }
    }
}
