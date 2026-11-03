namespace DistIL.Tests.Passes;

using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;
using DistIL.Analysis;
using System.Reflection;
using MethodBody = DistIL.IR.MethodBody;

[Collection("ModuleResolver")]
public class PassRegressionTests
{
    readonly ModuleResolver _modResolver;
    readonly ModuleDef _testAsm;

    public PassRegressionTests(ModuleResolverFixture mrf)
    {
        _modResolver = mrf.Resolver;
        _testAsm = _modResolver.Resolve("TestAsm");
    }

    // TODO: these should ideally be theories for each decl (+ fixture for parsed sources) to make debugging easier

    [Fact]
    public void Test_SimplifyInsts() => CheckIR("SimplifyInsts.ethil", new SimplifyInsts(_modResolver));

    [Fact]
    public void Test_SimplifyCFG() => CheckIR("SimplifyCFG.ethil", new SimplifyCFG());

    [Fact]
    public void Test_LoopStrengthReduction() => CheckIR("LoopStrengthReduction.ethil", new LoopStrengthReduction());

    [Fact]
    public void Test_ValueNumbering() => CheckIR("ValueNumbering.ethil", new ValueNumbering());

    [Fact]
    public void Test_AssertionProp() => CheckIR("AssertionProp.ethil", new AssertionProp());
    
    [Fact]
    public void Test_ILGenerator() => CheckCIL("CodeGen/StructFieldInsertExtract.ethil");

    [Fact]
    public void SsaPromotionPreservesPartialStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var block = body.CreateBlock();
        var slot = body.CreateVar(PrimType.Int32);
        block.InsertLast(new StoreInst(slot, ConstInt.CreateI(0x12345678)));
        block.InsertLast(new StoreInst(slot, ConstInt.CreateI(1), PrimType.Byte));
        var load = new LoadInst(slot);
        block.InsertLast(load);
        block.SetBranch(new ReturnInst(load));
        Run(body, new SsaPromotion());
        Assert.Equal(0x12345601, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void DeadCodeElimPreservesStoredLocalAddresses()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var block = body.CreateBlock();
        var value = body.CreateVar(PrimType.Int32);
        var reference = body.CreateVar(PrimType.Int32.CreateByref());
        var ib = new IRBuilder(block);
        ib.CreateStore(value, ConstInt.CreateI(5));
        ib.CreateStore(reference, value);
        ib.CreateReturn(ib.CreateLoad(ib.CreateLoad(reference)));
        Run(body, new DeadCodeElim());
        Assert.Equal(5, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void MemoryBarriersSurviveDceAndClobberAvailableLoads()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ib = new IRBuilder(body.CreateBlock());
        var address = ib.CreateFieldAddr(field);
        var first = ib.CreateLoad(address);
        var acquire = ib.CreateLoad(address, flags: PointerFlags.Volatile);
        var second = ib.CreateLoad(address);
        ib.CreateReturn(ib.CreateAdd(first, second));
        Run(body, new ValueNumbering(), new DeadCodeElim());
        Assert.NotNull(acquire.Block);
        Assert.NotNull(second.Block);
        Assert.True(acquire.IsMemoryBarrier);
        Assert.False(acquire.MayWriteToMemory);
    }

    [Fact]
    public void StaticFieldAccessRetainsTypeInitialization()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var initialized = body.Definition.Module.CreateType(null, "Initialized");
        var effect = type.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var value = initialized.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ctor = initialized.CreateMethod(".cctor", PrimType.Void, [], MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var init = new IRBuilder(ctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateFieldLoad(value);
        ib.CreateReturn(ib.CreateFieldLoad(effect));
        Run(body, new DeadCodeElim());
        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void ForestPreservesExceptionOrderWithStores()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ib = new IRBuilder(body.CreateBlock());
        var division = ib.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(10), body.Args[0]));
        ib.CreateFieldStore(field, null, ConstInt.CreateI(1));
        ib.CreateReturn(division);
        var il = ILGenerator.GenerateCode(body).Instructions.ToList();
        Assert.True(il.FindIndex(i => i.OpCode == ILCode.Div) < il.FindIndex(i => i.OpCode == ILCode.Stsfld));
    }

    [Fact]
    public void ValueNumberingPreservesVirtualCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var method = type.CreateMethod("Read", PrimType.Int32, [new ParamDef(type, "this")], MethodAttributes.Public | MethodAttributes.Virtual);
        method.Body = new MethodBody(method);
        method.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        var ib = new IRBuilder(body.CreateBlock());
        var first = ib.CreateCallVirt(method, ConstNull.Create());
        var second = ib.CreateCallVirt(method, ConstNull.Create());
        ib.CreateReturn(ib.CreateSub(second, first));
        Run(body, new ValueNumbering());
        Assert.NotNull(second.Block);
        Assert.Equal(2, body.Instructions().OfType<CallInst>().Count());
    }

    [Fact]
    public void RecursiveFunctionEffectsAreConservative()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        var type = body.Definition.DeclaringType;
        var other = type.CreateMethod("Other", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        other.Body = new MethodBody(other);
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateCall(other);
        var field = type.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        ib.CreateFieldStore(field, null, ConstInt.CreateI(1));
        ib.CreateReturn();
        var ob = new IRBuilder(other.Body.CreateBlock());
        ob.CreateCall(body.Definition);
        ob.CreateReturn();
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var effects = comp.GetAnalysis<GlobalFunctionEffects>();
        Assert.True(effects.GetEffects(body.Definition).MayWriteMem);
        Assert.True(effects.GetEffects(other).MayWriteMem);
    }

    [Fact]
    public void DictionaryLookupEffectsIncludeUserComparers()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        body.CreateBlock().SetBranch(new ReturnInst());
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var effects = comp.GetAnalysis<GlobalFunctionEffects>();
        var dict = _modResolver.Import(typeof(Dictionary<int,int>));
        Assert.True(effects.GetEffects(dict.FindMethod("get_Item")).MayWriteMem);
        Assert.True(effects.GetEffects(dict.FindMethod("ContainsKey")).MayWriteMem);
        Assert.False(effects.GetEffects(dict.FindMethod("get_Count")).MayWriteMem);
    }

    [Fact]
    public void ValueNumberingClobbersFieldsWhenTheirStructElementIsStored()
    {
        var cell = _modResolver.Import(typeof(ValueTuple<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, cell.CreateArray(), cell);
        var ib = new IRBuilder(body.CreateBlock());
        var element = ib.CreateArrayAddr(body.Args[0], ConstInt.CreateI(0));
        var field = ib.CreateFieldAddr(cell.FindField("Item1"), element);
        var first = ib.CreateLoad(field);
        ib.CreateStore(element, body.Args[1]);
        var second = ib.CreateLoad(field);
        ib.CreateReturn(ib.CreateAdd(first, second));
        Run(body, new ValueNumbering());
        Assert.Equal(7, Utils.Compile<Func<ValueTuple<int>[],ValueTuple<int>,int>>(body)([new(5)], new(2)));
    }

    [Fact]
    public void ValueNumberingClobbersCovariantInterfaceArrays()
    {
        var comparable = _modResolver.Import(typeof(IComparable));
        var convertible = _modResolver.Import(typeof(IConvertible));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, comparable.CreateArray(), convertible.CreateArray(), PrimType.String);
        var ib = new IRBuilder(body.CreateBlock());
        var first = ib.CreateArrayLoad(body.Args[0], ConstInt.CreateI(0));
        ib.CreateArrayStore(body.Args[1], ConstInt.CreateI(0), body.Args[2]);
        var second = ib.CreateArrayLoad(body.Args[0], ConstInt.CreateI(0));
        ib.CreateReturn(ib.CreateEq(first, second));
        Run(body, new ValueNumbering());
        string[] array = ["first"];
        Assert.False(Utils.Compile<Func<IComparable[],IConvertible[],string,bool>>(body)(array, array, "second"));
    }

    private static void Run(MethodBody body, params IMethodPass[] passes)
    {
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var ctx = new MethodTransformContext(comp, body);
        foreach (var pass in passes) pass.Run(ctx).InvalidateAffectedAnalyses(ctx);
        Assert.DoesNotContain(IRVerifier.Diagnose(body), d => d.Severity == DiagnosticSeverity.Error);
    }

    private void CheckIR(string filename, IMethodPass pass)
    {
        CheckDisasm(filename, (comp, body, sw) => {
            pass.Run(new MethodTransformContext(comp, body));

            IRPrinter.ExportPlain(body, sw);
            sw.Write("\n\n");
        });
    }
    
    private void CheckCIL(string filename)
    {
        CheckDisasm(filename, (comp, body, sw) => {
            var ilasm = ILGenerator.GenerateCode(body);

            sw.WriteLine($"// {body.Definition}");

            foreach (var inst in ilasm.Instructions) {
                sw.WriteLine(inst.ToString());
            }
            sw.WriteLine();
        });
    }
    
    private void CheckDisasm(string filename, Action<Compilation, MethodBody, StringWriter> processAndDisasm)
    {
        var selfType = _testAsm.CreateType("RegressionTests", $"_Test_{Path.GetFileNameWithoutExtension(filename)}");
        string source = File.ReadAllText("Passes/Cases/" + filename);

        string fixedSource = $"import RegressionTests from TestAsm\nimport Self = {selfType.Name}\n\n{source}";
        var ctx = IRParser.Populate(fixedSource, _modResolver);
        ctx.ThrowIfError();

        var decls = ctx.DeclaredMethods.ToDictionary(e => e.Definition.Name);

        var comp = new Compilation(_testAsm, new VoidLogger(), new CompilationSettings());
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

    // TODO: Fix importer cases
}
