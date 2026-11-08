namespace Cilica.Benchmarks;

using System.Reflection;
using BenchmarkDotNet.Attributes;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

[MemoryDiagnoser]
public class SsaPromotionBenchmarks
{
    [Params(2, 128, 1024)]
    public int Predecessors { get; set; }

    ModuleResolver _resolver = null!;
    Compilation _compilation = null!;
    MethodBody _template = null!;
    readonly SsaPromotion _pass = new();

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new ModuleResolver();
        _resolver.AddTrustedSearchPaths();
        var module = _resolver.Create("SsaPromotionBenchmark");
        var type = module.CreateType(null, "Methods");
        var method = type.CreateMethod("Select", PrimType.Int32, [new ParamDef(PrimType.Int32, "index")],
            MethodAttributes.Public | MethodAttributes.Static);
        _template = new MethodBody(method);
        var slot = _template.CreateVar(PrimType.Int32);
        var entry = _template.CreateBlock();
        var predecessors = Enumerable.Range(0, Predecessors).Select(_ => _template.CreateBlock()).ToArray();
        var merge = _template.CreateBlock();
        entry.SetBranch(new SwitchInst(_template.Args[0], predecessors[^1], predecessors[..^1]));

        for (int i = 0; i < predecessors.Length; i++) {
            predecessors[i].InsertLast(new StoreInst(slot, ConstInt.CreateI(i)));
            predecessors[i].SetBranch(merge);
        }
        var load = new LoadInst(slot);
        merge.InsertLast(load);
        merge.SetBranch(new ReturnInst(load));
        _compilation = new Compilation(module, new VoidLogger(), new());
    }

    [Benchmark]
    public MethodPassResult PromoteMerge()
        => _pass.Run(new MethodTransformContext(_compilation, IRCloner.CloneBody(_template)));

    [GlobalCleanup]
    public void Cleanup() => _resolver.Dispose();
}
