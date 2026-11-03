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

    [Theory]
    [InlineData(UnaryOp.Neg)]
    [InlineData(UnaryOp.Not)]
    [InlineData(UnaryOp.FNeg)]
    public void SimplifyUnaryCancelsBothOperations(UnaryOp op)
    {
        var type = op == UnaryOp.FNeg ? PrimType.Double : PrimType.Int32;
        var body = Utils.CreateTestMethod(_modResolver, type, type);
        var ib = new IRBuilder(body.CreateBlock());
        var inner = ib.Emit(new UnaryInst(op, body.Args[0]));
        var outer = ib.Emit(new UnaryInst(op, inner));
        var ret = ib.CreateReturn(outer);
        Run(body, new SimplifyInsts(_modResolver));
        Assert.Same(body.Args[0], ret.Value);
    }

    [Fact]
    public void InliningPreservesCallVirtNullChecks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var callee = type.CreateMethod("Constant", PrimType.Int32, [new ParamDef(type, "this")]);
        callee.Body = new MethodBody(callee);
        callee.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateReturn(ib.CreateCallVirt(callee, ConstNull.Create()));
        Run(body, new InlineMethods(), new DeadCodeElim());
        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        Assert.Throws<NullReferenceException>(() => Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void InliningPreservesStaticTypeInitialization()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var initialized = body.Definition.Module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var cctor = initialized.CreateMethod(".cctor", PrimType.Void, [], MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        cctor.Body = new MethodBody(cctor);
        var init = new IRBuilder(cctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();
        var callee = initialized.CreateMethod("Noop", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        callee.Body.CreateBlock().SetBranch(new ReturnInst());
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateCall(callee);
        ib.CreateReturn(ib.CreateFieldLoad(effect));
        Run(body, new InlineMethods(), new DeadCodeElim());
        Assert.DoesNotContain(body.Instructions().OfType<CallInst>(), c => c.Method == callee);
        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ScalarReplacementHonorsAddressEscapeAndFinalization(bool escape, bool finalizer)
    {
        var body = Utils.CreateTestMethod(_modResolver, escape ? PrimType.Int32.CreateByref() : PrimType.Int32);
        var holder = body.Definition.Module.CreateType(null, "Holder");
        var field = holder.CreateField("Value", PrimType.Int32);
        var ctor = holder.CreateMethod(".ctor", PrimType.Void, [new ParamDef(holder, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var cb = new IRBuilder(ctor.Body.CreateBlock());
        cb.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), ctor.Body.Args[0]);
        cb.CreateReturn();
        if (finalizer) {
            var finalize = holder.CreateMethod("Finalize", PrimType.Void, [new ParamDef(holder, "this")], MethodAttributes.Family | MethodAttributes.Virtual);
            finalize.Body = new MethodBody(finalize);
            finalize.Body.CreateBlock().SetBranch(new ReturnInst());
        }
        var ib = new IRBuilder(body.CreateBlock());
        var allocation = ib.CreateNewObj(ctor);
        var address = ib.CreateFieldAddr(field, allocation);
        ib.CreateStore(address, ConstInt.CreateI(42));
        ib.CreateReturn(escape ? address : ib.CreateLoad(address));
        Run(body, new ScalarReplacement(), new SsaPromotion());
        Assert.Equal(escape || finalizer, allocation.Block != null);
        if (!escape && !finalizer) Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void SimplifyCfgStoresSinglePrecisionSwitchBits()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Single, PrimType.Int32);
        var entry = body.CreateBlock();
        var a = body.CreateBlock(); var b = body.CreateBlock(); var fallback = body.CreateBlock(); var merge = body.CreateBlock();
        entry.SetBranch(new SwitchInst(body.Args[0], fallback, a, b));
        a.SetBranch(merge); b.SetBranch(merge); fallback.SetBranch(merge);
        var phi = merge.InsertPhi(new PhiInst(PrimType.Single, (a, ConstFloat.CreateS(1)), (b, ConstFloat.CreateS(2)), (fallback, ConstFloat.CreateS(-0f))));
        merge.SetBranch(new ReturnInst(phi));
        Run(body, new SimplifyCFG());
        var run = Utils.Compile<Func<int,float>>(body);
        Assert.Equal(1, run(0)); Assert.Equal(2, run(1));
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(run(-1)));
    }

    [Fact]
    public void AssertionPropagationUsesIncomingPhiEdgePolarity()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, PrimType.Object, PrimType.Object);
        var entry = body.CreateBlock(); var other = body.CreateBlock(); var merge = body.CreateBlock(); var exit = body.CreateBlock();
        var first = new IRBuilder(entry); first.SetBranch(first.CreateNe(body.Args[0], ConstNull.Create()), other, merge);
        var second = new IRBuilder(other); second.SetBranch(second.CreateNe(body.Args[1], ConstNull.Create()), exit, merge);
        exit.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        var ib = new IRBuilder(merge);
        var phi = ib.CreatePhi(PrimType.Object, (entry, body.Args[0]), (other, body.Args[1]));
        ib.CreateReturn(ib.CreateEq(phi, ConstNull.Create()));
        Run(body, new AssertionProp());
        var run = Utils.Compile<Func<object?,object?,bool>>(body);
        Assert.True(run(null, null)); Assert.True(run(new object(), null)); Assert.False(run(new object(), new object()));
    }

    [Fact]
    public void LoopStrengthReductionRetainsCovariantArrayStoreChecks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void, PrimType.Object.CreateArray(), PrimType.Object);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        loop.Build(h => h.CreateSlt(index, h.CreateConvert(h.CreateArrayLen(body.Args[0]), PrimType.Int32)),
            b => b.CreateArrayStore(body.Args[0], index, body.Args[1]));
        loop.Exit.CreateReturn();
        Run(body, new LoopStrengthReduction());
        Assert.Single(body.Instructions().OfType<ArrayAddrInst>());
        var run = Utils.Compile<Action<object[],object>>(body);
        Assert.Throws<ArrayTypeMismatchException>(() => run(new string[1], new object()));
        object[] ordinary = new object[1]; var value = new object(); run(ordinary, value); Assert.Same(value, ordinary[0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DictionaryLookupCoalescingRequiresProvenComparerAndEdge(bool bypassEdge, bool subclass)
    {
        var dictionary = _modResolver.Import(typeof(Dictionary<string,int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool, PrimType.String);
        var entry = body.CreateBlock(); var check = body.CreateBlock(); var read = body.CreateBlock(); var missing = body.CreateBlock();
        var ib = new IRBuilder(entry);
        MethodDesc ctor = dictionary.FindMethod(".ctor", new MethodSig(PrimType.Void, []));
        if (subclass) {
            var derived = body.Definition.Module.CreateType(null, "DerivedDictionary", baseType: dictionary);
            var constructor = derived.CreateMethod(".ctor", PrimType.Void, [new ParamDef(derived, "this")],
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            constructor.Body = new MethodBody(constructor);
            var init = new IRBuilder(constructor.Body.CreateBlock());
            init.CreateCall(ctor, constructor.Body.Args[0]);
            init.CreateReturn();
            ctor = constructor;
        }
        var dict = ib.CreateNewObj(ctor);
        ib.CreateCallVirt("Add", dict, body.Args[1], ConstInt.CreateI(7));
        if (bypassEdge) ib.SetBranch(body.Args[0], read, check); else ib.SetBranch(check);
        var cb = new IRBuilder(check);
        cb.SetBranch(cb.CreateCallVirt("ContainsKey", dict, body.Args[1]), read, missing);
        var rb = new IRBuilder(read);
        rb.CreateReturn(rb.CreateCallVirt("get_Item", dict, body.Args[1]));
        missing.SetBranch(new ReturnInst(ConstInt.CreateI(-1)));
        Run(body, new SimplifyInsts(_modResolver));
        Assert.Equal(!bypassEdge && !subclass, body.Instructions().OfType<CallInst>().Any(c => c.Method.Name == "TryGetValue"));
        var run = Utils.Compile<Func<bool,string,int>>(body);
        Assert.Equal(7, run(true, "key")); Assert.Equal(7, run(false, "key"));
    }

    private static void Run(MethodBody body, params IMethodPass[] passes)
    {
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var ctx = new MethodTransformContext(comp, body);
        foreach (var pass in passes) pass.Run(ctx).InvalidateAffectedAnalyses(ctx);
        Assert.DoesNotContain(IRVerifier.Diagnose(body), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DeadCodeElimPreservesDivergingCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var spin = body.Definition.DeclaringType.CreateMethod("Spin", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        spin.Body = new MethodBody(spin);
        var entry = spin.Body.CreateBlock();
        var loop = spin.Body.CreateBlock();
        entry.SetBranch(loop);
        loop.SetBranch(loop);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(spin);
        builder.CreateReturn(ConstInt.CreateI(42));
        Run(body, new DeadCodeElim());

        Assert.NotNull(call.Block);
    }

    [Fact]
    public void ValueNumberingPreservesAmbientCultureAndStringAllocation()
    {
        var culture = _modResolver.Import(typeof(System.Globalization.CultureInfo));
        var textType = _modResolver.Import(typeof(string));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, culture);
        var builder = new IRBuilder(body.CreateBlock());
        var upper = textType.FindMethod("ToUpper", new MethodSig(PrimType.String, []));
        var setter = culture.FindMethod("set_CurrentCulture", new MethodSig(PrimType.Void, [culture]));
        var equal = textType.FindMethod("op_Equality", new MethodSig(PrimType.Bool, [PrimType.String, PrimType.String]));
        var first = builder.CreateCallVirt(upper, ConstString.Create("i"));
        builder.CreateCall(setter, body.Args[0]);
        var second = builder.CreateCallVirt(upper, first.Args[0]);
        builder.CreateReturn(builder.CreateCall(equal, first, second));
        Run(body, new ValueNumbering());

        Assert.NotNull(second.Block);

        if (!(AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)) {
            var saved = System.Globalization.CultureInfo.CurrentCulture;

            try {
                System.Globalization.CultureInfo.CurrentCulture = new("en-US");

                Assert.False(Utils.Compile<Func<System.Globalization.CultureInfo, bool>>(body)(new("tr-TR")));
            } finally {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }

        var effects = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<GlobalFunctionEffects>();

        Assert.True(effects.GetEffects(textType.FindMethod("ToUpperInvariant", new MethodSig(PrimType.String, []))).MayAllocate);
    }

    [Fact]
    public void InliningBoundsMutualRecursionWithProfitableArguments()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var parameters = Enumerable.Range(0, 10).Select(i => new ParamDef(PrimType.Int32, "p" + i)).ToImmutableArray();
        var a = type.CreateMethod("A", PrimType.Int32, parameters, MethodAttributes.Public | MethodAttributes.Static);
        var b = type.CreateMethod("B", PrimType.Int32, parameters, MethodAttributes.Public | MethodAttributes.Static);
        a.Body = new MethodBody(a);
        b.Body = new MethodBody(b);
        var ia = new IRBuilder(a.Body.CreateBlock());
        ia.CreateReturn(ia.CreateCall(b, a.Body.Args.Cast<Value>().ToArray()));
        var ib = new IRBuilder(b.Body.CreateBlock());
        ib.CreateReturn(ib.CreateCall(a, b.Body.Args.Cast<Value>().ToArray()));
        var root = new IRBuilder(body.CreateBlock());
        root.CreateReturn(root.CreateCall(a, Enumerable.Repeat<Value>(ConstInt.CreateI(1), 10).ToArray()));
        Run(body, new InlineMethods(new() { InitialBudget = 1, BudgetFactor = 0 }));

        Assert.Single(body.Instructions().OfType<CallInst>());
        Assert.True(body.Instructions().Count() < 10);
    }

    [Fact]
    public void ChangeReportingTracksPhiEliminationAndNoopSimplification()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var left = body.CreateBlock();
        var right = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], left, right));
        left.SetBranch(exit);
        right.SetBranch(exit);
        var phi = exit.InsertPhi(new PhiInst(PrimType.Int32, (left, ConstInt.CreateI(1)), (right, ConstInt.CreateI(1))));
        exit.SetBranch(new ReturnInst(phi));
        var ctx = new MethodTransformContext(new Compilation(body.Definition.Module, new VoidLogger(), new()), body);

        Assert.Equal(MethodInvalidations.DataFlow, new DeadCodeElim().Run(ctx).Changes);
        Assert.Null(phi.Block);
        Assert.Equal(MethodInvalidations.None, new SimplifyInsts(_modResolver).Run(ctx).Changes);
    }

    private void CheckIR(string filename, IMethodPass pass)
    {
        CheckDisasm(filename, (comp, body, sw) => {
            PassRunner.Run(pass, new MethodTransformContext(comp, body));
            IRVerifier.Verify(body);

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
