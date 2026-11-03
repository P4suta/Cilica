namespace DistIL.Benchmarks;

using System.Reflection;
using BenchmarkDotNet.Attributes;
using DistIL.AsmIO;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using MethodBody = DistIL.IR.MethodBody;

[MemoryDiagnoser]
public class CompilerPasses
{
    [Params(1000, 4000, 8000)]
    public int Loads { get; set; }

    ModuleResolver _resolver = null!;
    Compilation _compilation = null!;
    MethodBody _template = null!;
    MethodTransformContext _context = null!;
    readonly ValueNumbering _pass = new();

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new ModuleResolver();
        _resolver.AddTrustedSearchPaths();
        var module = _resolver.Create("CompilerBenchmark");
        var type = module.CreateType(null, "Methods");
        var method = type.CreateMethod("Sum", PrimType.Int32, [new ParamDef(PrimType.Int32.CreateByref(), "source")],
            MethodAttributes.Public | MethodAttributes.Static);
        _template = new MethodBody(method);
        var builder = new IRBuilder(_template.CreateBlock());
        Value sum = ConstInt.CreateI(0);

        for (int i = 0; i < Loads; i++) {
            sum = builder.CreateAdd(sum, builder.CreateLoad(_template.Args[0]));
        }

        builder.CreateReturn(sum);
        _compilation = new Compilation(module, new VoidLogger(), new());
    }

    [IterationSetup]
    public void Reset() => _context = new(_compilation, IRCloner.CloneBody(_template));

    [Benchmark]
    public MethodPassResult RepeatedLoads() => _pass.Run(_context);

    [GlobalCleanup]
    public void Cleanup() => _resolver.Dispose();
}
