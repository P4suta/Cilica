namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class InlineMethodsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    public static class Features
    {
        [FeatureSwitchDefinition("Cilica.Tests.IsEnabled")]
        public static bool Switch => true;

        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        public static bool Guard => true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeatureGettersRemainSubstitutableAfterOptimization(bool featureGuard)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var getter = type.CreateMethod("get_IsEnabled", PrimType.Bool, [],
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName);
        getter.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        getter.Body = new MethodBody(getter);
        getter.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(1)));
        var property = new PropertyDef(type, "IsEnabled", new MethodSig(PrimType.Bool, [], isInstance: false), getter);
        var sourceProperty = ((TypeDef)_modResolver.Import(typeof(Features))).Properties
            .Single(property => property.Name == (featureGuard ? nameof(Features.Guard) : nameof(Features.Switch)));
        property.GetCustomAttribs(readOnly: false).Add(Assert.Single(sourceProperty.GetCustomAttribs()));
        type.Properties.Add(property);
        var entry = body.CreateBlock();
        var enabled = body.CreateBlock();
        var disabled = body.CreateBlock();
        var builder = new IRBuilder(entry);
        builder.SetBranch(builder.CreateCall(getter), enabled, disabled);
        enabled.SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        disabled.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });

        var result = CompilationPipeline.CreateDefault(compilation).Run([getter, body.Definition]);

        Assert.True(result.Succeeded);
        Assert.Equal(42, Utils.Compile<Func<int>>(body.Definition.Body!)());
        getter.Body = new MethodBody(getter);
        getter.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        Assert.Equal(0, Utils.Compile<Func<int>>(body.Definition.Body!)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlinedPointerParametersPreserveManagedFieldAddresses(bool voidPointer)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var field = type.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var pointer = (voidPointer ? PrimType.Void : PrimType.Int32).CreatePointer();
        var helper = type.CreateMethod("Increment", PrimType.Void, [new ParamDef(pointer, "address")],
            MethodAttributes.Public | MethodAttributes.Static);
        helper.Body = new MethodBody(helper);
        var callee = new IRBuilder(helper.Body.CreateBlock());
        var value = callee.Emit(new LoadInst(helper.Body.Args[0], PrimType.Int32));
        callee.Emit(new StoreInst(helper.Body.Args[0], callee.CreateAdd(value, ConstInt.CreateI(1)), PrimType.Int32));
        callee.CreateReturn();

        var builder = new IRBuilder(body.CreateBlock());
        var address = builder.CreateFieldAddr(field);
        builder.CreateStore(address, body.Args[0]);
        builder.CreateCall(helper, address);
        builder.CreateReturn(builder.CreateLoad(address));
        Run(body, new InlineMethods());

        Assert.Empty(body.Instructions().OfType<CallInst>());
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(43, run(42));
        Assert.Equal(int.MinValue, run(int.MaxValue));
    }

    [Theory]
    [InlineData(null, true, MethodImplAttributes.IL, true)]
    [InlineData("Dependency", true, MethodImplAttributes.IL, true)]
    [InlineData("System.Dependency", true, MethodImplAttributes.IL, true)]
    [InlineData("Dependency", false, MethodImplAttributes.IL, false)]
    [InlineData("System.Dependency", false, MethodImplAttributes.AggressiveInlining, false)]
    [InlineData(null, true, MethodImplAttributes.NoInlining, false)]
    [InlineData(null, true, MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveInlining, false)]
    public void UnimportedHelpersFoldWhenInliningIsAllowed(string? assemblyName, bool crossAssembly, MethodImplAttributes attributes, bool inline)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var module = assemblyName == null ? body.Definition.Module : _modResolver.Create(assemblyName + "_" + Guid.NewGuid().ToString("N"));
        var type = module.CreateType(null, "Helper");
        var target = type.CreateMethod("Transform", PrimType.Int32, [new ParamDef(PrimType.Int32, "value")],
            MethodAttributes.Public | MethodAttributes.Static);
        target.ImplAttribs = attributes;
        var il = new ILAssembler();
        il.Emit(ILCode.Ldarg_0);
        il.Emit(ILCode.Ldc_I4_2);
        il.Emit(ILCode.Mul);
        il.Emit(ILCode.Ldc_I4_2);
        il.Emit(ILCode.Add);
        il.Emit(ILCode.Ret);
        target.ILBody = il.Assemble(null);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(target, ConstInt.CreateI(20));
        builder.CreateReturn(call);
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            AllowCrossAssemblyIPO = crossAssembly, VerifyEachPass = true
        });

        PassRunner.Run(new InlineMethods(), new MethodTransformContext(compilation, body));

        if (inline) {
            Assert.Equal(42, Assert.IsType<ConstInt>(Assert.Single(body.Instructions().OfType<ReturnInst>()).Value).Value);
            Assert.Empty(body.Instructions().OfType<CallInst>());
            Assert.Equal(42, Utils.Compile<Func<int>>(body)());
        } else {
            Assert.Same(call, Assert.Single(body.Instructions().OfType<CallInst>()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SystemAssemblyInliningHonorsAggressiveInlining(bool imported)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var module = _modResolver.Create("System.Dependency_" + Guid.NewGuid().ToString("N"));
        var target = module.CreateType(null, "Helper").CreateMethod("Mix", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        target.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        target.Body = new MethodBody(target);
        var builder = new IRBuilder(target.Body.CreateBlock());
        Value value = target.Body.Args[0];
        for (int i = 0; i < 16; i++) value = builder.CreateXor(builder.CreateMul(value, ConstInt.CreateI(17)), ConstInt.CreateI(i + 19));
        builder.CreateReturn(value);
        target.ILBody = ILGenerator.GenerateCode(target.Body);
        if (!imported) target.Body = null;
        builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(target, body.Args[0]));

        Run(body, new InlineMethods());

        Assert.Empty(body.Instructions().OfType<CallInst>());
        int expected = 7;
        for (int i = 0; i < 16; i++) expected = unchecked(expected * 17) ^ (i + 19);
        Assert.Equal(expected, Utils.Compile<Func<int, int>>(body)(7));
    }

    [Fact]
    public void InliningPreferencePreservesConstantFoldingBenefits()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(body.Args[0], ConstInt.CreateI(1)));
        var advisor = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<InliningAdvisor>();
        int cost = advisor.EvaluateInliningCost(body, [ConstInt.CreateI(41)]);
        Assert.True(cost < 0);

        body.Definition.ImplAttribs = MethodImplAttributes.AggressiveInlining;

        Assert.True(advisor.EvaluateInliningCost(body, [ConstInt.CreateI(41)]) <= cost);
    }

    public interface ICounter { int Next(); }
    public struct Counter : ICounter
    {
        int _value;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int Next() => ++_value;
    }
    public class Reader
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public virtual int Read() => 42;
    }
    public sealed class SealedReader : Reader { }

    public abstract class FactoryBase
    {
        public abstract ProductBase Create();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FactoryBase Start() => Forward();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static FactoryBase Forward() => new ConcreteFactory();
    }
    public sealed class ConcreteFactory : FactoryBase
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ProductBase Create() => Forward();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ProductBase Forward() => new ConcreteProduct();
    }
    public abstract class ProductBase
    {
        public abstract int Read();
        public abstract ProductBase Next();
    }
    public sealed class ConcreteProduct : ProductBase
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Read() => 42;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ProductBase Next() => Forward();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ProductBase Forward() => new ConcreteProduct();
    }

    [Fact]
    public void CallOptimizationRevisitsConsumersAfterFactoryInlining()
    {
        var body = CreateFactoryChain();
        Run(body, new InlineMethods(new() { InitialBudget = 1000, BudgetFactor = 0 }));
        Assert.Empty(body.Instructions().OfType<CallInst>());
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
        long revision = body.Revision;
        Run(body, new InlineMethods());
        Assert.Equal(revision, body.Revision);
    }

    [Fact]
    public void CallOptimizationConvergesAcrossLongDependentChains()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var type = _modResolver.Import(typeof(ProductBase));
        Value product = builder.CreateNewObj(_modResolver.Import(typeof(ConcreteProduct)).FindMethod(".ctor"));
        for (int i = 0; i < 24; i++) product = builder.CreateCallVirt(type.FindMethod("Next"), product);
        builder.CreateReturn(builder.CreateCallVirt(type.FindMethod("Read"), product));
        Run(body, new InlineMethods(new() { InitialBudget = 10000, BudgetFactor = 0 }));
        Assert.Empty(body.Instructions().OfType<CallInst>());
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void FixedPointDoesNotReplenishTheExpansionBudget()
    {
        var body = CreateFactoryChain();
        Run(body, new InlineMethods(new() { MaxExpansions = 2, InitialBudget = 1000, BudgetFactor = 0 }));
        Assert.Contains(body.Instructions().OfType<CallInst>(), call => call.Method.Name == "Create");
        Assert.Contains(body.Instructions().OfType<CallInst>(), call => call.Method.Name == "Read");
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ILSizeBudgetAccountsForSwitchTablesAfterSpecialization(bool constantArgument)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var target = body.Definition.DeclaringType.CreateMethod("Select", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "index")], MethodAttributes.Public | MethodAttributes.Static);
        target.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        target.Body = new MethodBody(target);
        var entry = target.Body.CreateBlock();
        var even = target.Body.CreateBlock();
        var odd = target.Body.CreateBlock();
        entry.SetBranch(new SwitchInst(target.Body.Args[0], even,
            Enumerable.Range(0, 8192).Select(i => i % 2 == 0 ? even : odd).ToArray()));
        var evenBuilder = new IRBuilder(even);
        evenBuilder.CreateReturn(evenBuilder.CreateAdd(target.Body.Args[0], ConstInt.CreateI(10)));
        var oddBuilder = new IRBuilder(odd);
        oddBuilder.CreateReturn(oddBuilder.CreateAdd(target.Body.Args[0], ConstInt.CreateI(41)));
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(target, constantArgument ? ConstInt.CreateI(1) : body.Args[0]));

        RunWithSizeBudget(body, 512, new InlineMethods());

        Assert.Equal(!constantArgument, body.Instructions().OfType<CallInst>().Any());
        Assert.InRange(ILGenerator.GenerateCode(body).GetCodeSize(), 1, 512);
        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(constantArgument ? 42 : 10, run(0));
        Assert.Equal(42, run(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerILSizeBudgetSurvivesRepeatedInliningPasses(bool voidCall)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Result", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var target = body.Definition.DeclaringType.CreateMethod("Mix", voidCall ? PrimType.Void : PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        target.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        target.Body = new MethodBody(target);
        var builder = new IRBuilder(target.Body.CreateBlock());
        Value value = target.Body.Args[0];
        for (int i = 0; i < 16; i++) value = builder.CreateXor(builder.CreateMul(value, ConstInt.CreateI(17)), ConstInt.CreateI(i + 19));
        if (voidCall) builder.CreateStore(builder.CreateFieldAddr(field), value);
        builder.CreateReturn(voidCall ? null : value);
        builder = new IRBuilder(body.CreateBlock());
        value = body.Args[0];
        for (int i = 0; i < 24; i++) {
            var call = builder.CreateCall(target, value);
            value = voidCall ? builder.CreateLoad(builder.CreateFieldAddr(field)) : call;
        }
        builder.CreateReturn(value);

        for (int pass = 0; pass < 3; pass++) {
            RunWithSizeBudget(body, 512, new InlineMethods(new() { InitialBudget = 100000, BudgetFactor = 0 }));
            Assert.InRange(ILGenerator.GenerateCode(body).GetCodeSize(), 1, 512);
            Assert.InRange(body.Instructions().OfType<CallInst>().Count(), 1, 23);
        }

        int expected = 7;
        for (int i = 0; i < 24; i++) {
            for (int j = 0; j < 16; j++) expected = unchecked(expected * 17) ^ (j + 19);
        }
        Assert.Equal(expected, Utils.Compile<Func<int, int>>(body)(7));
    }

    [Fact]
    public void LargeCallersCanStillInlineSizeReducingWrappers()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var target = body.Definition.DeclaringType.CreateMethod("Identity", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        target.Body = new MethodBody(target);
        target.Body.CreateBlock().SetBranch(new ReturnInst(target.Body.Args[0]));
        var entry = body.CreateBlock();
        var even = body.CreateBlock();
        var odd = body.CreateBlock();
        var value = new IRBuilder(entry).CreateCall(target, body.Args[0]);
        entry.SetBranch(new SwitchInst(value, even,
            Enumerable.Range(0, 1024).Select(i => i % 2 == 0 ? even : odd).ToArray()));
        var evenBuilder = new IRBuilder(even);
        evenBuilder.CreateReturn(evenBuilder.CreateAdd(body.Args[0], ConstInt.CreateI(10)));
        var oddBuilder = new IRBuilder(odd);
        oddBuilder.CreateReturn(oddBuilder.CreateAdd(body.Args[0], ConstInt.CreateI(41)));
        int before = ILGenerator.GenerateCode(body).GetCodeSize();

        RunWithSizeBudget(body, 512, new InlineMethods());

        Assert.Empty(body.Instructions().OfType<CallInst>());
        Assert.True(ILGenerator.GenerateCode(body).GetCodeSize() < before);
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body)(1));
    }

    void RunWithSizeBudget(MethodBody body, int limit, params IMethodPass[] passes)
    {
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            MaxInlinedMethodILSize = limit, VerifyEachPass = true
        });
        var context = new MethodTransformContext(compilation, body);
        foreach (var pass in passes) PassRunner.Run(pass, context);
    }

    MethodBody CreateFactoryChain()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var factoryType = _modResolver.Import(typeof(FactoryBase));
        var factory = builder.CreateCall(factoryType.FindMethod("Start"));
        var product = builder.CreateCallVirt(factoryType.FindMethod("Create"), factory);
        builder.CreateReturn(builder.CreateCallVirt(_modResolver.Import(typeof(ProductBase)).FindMethod("Read"), product));
        return body;
    }

    [Fact]
    public void DelegateSimplificationRetainsRecursiveInliningAncestry()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var recursive = body.Definition.DeclaringType.CreateMethod("Recursive", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        recursive.Body = new MethodBody(recursive);
        var builder = new IRBuilder(recursive.Body.CreateBlock());
        var delegateType = _modResolver.Import(typeof(Func<int, int>));
        var address = builder.Emit(new FuncAddrInst(recursive));
        var function = builder.CreateNewObj(delegateType.FindMethod(".ctor"), ConstNull.Create(), address);
        var invocation = builder.CreateCallVirt(delegateType.FindMethod("Invoke"), function, recursive.Body.Args[0]);
        builder.CreateReturn(builder.CreateAdd(invocation, ConstInt.CreateI(1)));
        var root = new IRBuilder(body.CreateBlock());
        root.CreateReturn(root.CreateCall(recursive, ConstInt.CreateI(0)));

        Run(body, new InlineMethods(new() { InitialBudget = 1000, BudgetFactor = 0 }));
        Assert.Equal(recursive, Assert.Single(body.Instructions().OfType<CallInst>()).Method);
        var result = Assert.IsType<BinaryInst>(Assert.Single(body.Instructions().OfType<ReturnInst>()).Value);
        Assert.Equal(1, Assert.IsType<ConstInt>(result.Right).Value);
    }

    [Fact]
    public void DevirtualizationUsesExactPhiInputsWithoutLosingNullChecks()
    {
        var type = _modResolver.Import(typeof(SealedReader));
        var method = _modResolver.Import(typeof(Reader)).FindMethod("Read");
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var entry = body.CreateBlock();
        var present = body.CreateBlock();
        var absent = body.CreateBlock();
        var merge = body.CreateBlock();
        entry.SetBranch(new BranchInst(body.Args[0], present, absent));
        var allocation = new IRBuilder(present).CreateNewObj(type.FindMethod(".ctor"));
        present.SetBranch(merge);
        absent.SetBranch(merge);
        var receiver = merge.InsertPhi(new PhiInst(method.DeclaringType,
            (present, allocation), (absent, ConstNull.Create())));
        var builder = new IRBuilder(merge, InsertionDir.After);
        var call = builder.CreateCallVirt(method, receiver);
        builder.CreateReturn(call);
        Run(body, new Devirtualize());
        Assert.False(call.IsVirtual);
        var compiled = Utils.Compile<Func<bool, int>>(body);
        Assert.Equal(42, compiled(true));
        Assert.Throws<NullReferenceException>(() => compiled(false));
    }

    [Fact]
    public void VersionSafeCallsDoNotReuseImportedForeignBodies()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var library = _modResolver.Create("Replaceable_" + Guid.NewGuid().ToString("N"));
        var owner = library.CreateType(null, "Library", TypeAttributes.Sealed);
        var method = owner.CreateMethod("Value", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        method.Body = new MethodBody(method);
        method.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(method);
        builder.CreateReturn(call);
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { AllowCrossAssemblyIPO = false });
        PassRunner.Run(new InlineMethods(), new MethodTransformContext(compilation, body));
        Assert.Contains(call, body.Instructions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticInterfaceDevirtualizationPreservesInitialization(bool explicitImplementation)
    {
        Assert.Equal(Execute(false), Execute(true));

        int Execute(bool devirtualize)
        {
            var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
            var module = body.Definition.Module;
            var itf = (TypeDefOrSpec)_modResolver.Import(typeof(Cilica.Tests.Util.MethodResolvingTests.IStaticDefault));
            var slot = itf.FindMethod("Read");
            var type = module.CreateType(null, "Implementation");
            type.Interfaces.Add(itf);
            var counter = body.Definition.DeclaringType.CreateField("Counter", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
            var initializer = type.CreateMethod(".cctor", PrimType.Void, [],
                MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            initializer.Body = new MethodBody(initializer);
            var initialize = new IRBuilder(initializer.Body.CreateBlock());
            initialize.CreateFieldStore(counter, null, ConstInt.CreateI(100));
            initialize.CreateReturn();
            if (explicitImplementation) {
                var implementation = type.CreateMethod("Read", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
                implementation.Body = new MethodBody(implementation);
                implementation.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(11)));
                type.MethodImpls.Add(slot, implementation);
            }
            var builder = new IRBuilder(body.CreateBlock());
            var call = builder.Emit(new CallInst(slot, [], constraint: type));
            builder.CreateReturn(builder.CreateAdd(call, builder.CreateFieldLoad(counter)));
            if (devirtualize) {
                Run(body, new Devirtualize());
                Assert.Null(call.Constraint);
            }
            return Utils.Compile<Func<int>>(body)();
        }
    }

    [Fact]
    public void UnsealedReferenceConstraintDoesNotProveAnExactReceiver()
    {
        var type = _modResolver.Import(typeof(Reader));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type.CreateByref());
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.Emit(new CallInst(type.FindMethod("Read"), [body.Args[0]], true, type));
        builder.CreateReturn(call);
        Run(body, new Devirtualize());
        Assert.True(call.IsVirtual);
        Assert.Equal(type, call.Constraint);
    }

    [Fact]
    public void NullableBoxKeepsTheCallvirtNullCheck()
    {
        var type = _modResolver.Import(typeof(int?));
        var body = Utils.CreateTestMethod(_modResolver, _modResolver.Import(typeof(Type)), type);
        var builder = new IRBuilder(body.CreateBlock());
        var receiver = builder.Emit(new CilIntrinsic.Box(type, body.Args[0]));
        var call = builder.CreateCallVirt(_modResolver.Import(typeof(object)).FindMethod("GetType"), receiver);
        builder.CreateReturn(call);
        Run(body, new Devirtualize());
        Assert.False(call.IsVirtual);
        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var compiled = Utils.Compile<Func<int?, Type>>(body);
        Assert.Equal(typeof(int), compiled(1));
        Assert.Throws<NullReferenceException>(() => compiled(null));
    }

    [Fact]
    public void DevirtualizationDoesNotRequireInliningAndPreservesNullChecks()
    {
        var type = _modResolver.Import(typeof(SealedReader));
        var method = _modResolver.Import(typeof(Reader)).FindMethod("Read");
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCallVirt(method, body.Args[0]);
        builder.CreateReturn(call);
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { EnableInlining = false });
        PassRunner.Run(new Devirtualize(), new MethodTransformContext(compilation, body));
        Assert.False(call.IsVirtual);
        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var compiled = Utils.Compile<Func<SealedReader, int>>(body);
        Assert.Equal(42, compiled(new SealedReader()));
        Assert.Throws<NullReferenceException>(() => compiled(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevirtualizationPreservesStructReceiverStorage(bool boxed)
    {
        var type = _modResolver.Import(typeof(Counter));
        var method = _modResolver.Import(typeof(ICounter)).FindMethod("Next");
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var local = body.CreateVar(type);
        builder.CreateInitObj(local);
        Value receiver = boxed ? builder.Emit(new CilIntrinsic.Box(type, builder.CreateLoad(local))) : local;
        var first = builder.Emit(new CallInst(method, [receiver], true, boxed ? null : type));
        var second = builder.Emit(new CallInst(method, [receiver], true, boxed ? null : type));
        builder.CreateReturn(builder.CreateAdd(first, second));
        Run(body, new Devirtualize());
        Assert.False(first.IsVirtual);
        Assert.False(second.IsVirtual);
        Assert.Null(first.Constraint);
        Assert.Equal(3, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InliningPreservesTailCallBoundaries(bool callerTail)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var owner = body.Definition.DeclaringType;
        var leaf = owner.CreateMethod("Leaf", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        leaf.ImplAttribs |= MethodImplAttributes.NoInlining;
        leaf.Body = new MethodBody(leaf);
        leaf.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var target = owner.CreateMethod("Forward", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        target.Body = new MethodBody(target);
        var tb = new IRBuilder(target.Body.CreateBlock());
        var forwarded = tb.Emit(new CallInst(leaf, []) { IsTailCall = !callerTail });
        tb.CreateReturn(forwarded);
        var ib = new IRBuilder(body.CreateBlock());
        var call = ib.Emit(new CallInst(target, []) { IsTailCall = callerTail });
        ib.CreateReturn(callerTail ? call : ib.CreateAdd(call, ConstInt.CreateI(1)));

        Run(body, new InlineMethods());
        Assert.Contains(call, body.Instructions());
        Assert.Equal(callerTail ? 42 : 43, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicGenericCallsAccountForNonPublicTypeArguments(bool nested)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool);
        var library = _modResolver.Create("PrivateGeneric_" + Guid.NewGuid().ToString("N"));
        var hidden = library.CreateType("Tests", "Hidden", TypeAttributes.NotPublic);
        if (nested) hidden = hidden.CreateNestedType("Nested", TypeAttributes.NestedPublic);
        var helper = _modResolver.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers))
            .FindMethod("IsReferenceOrContainsReferences").GetSpec([hidden]);
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateReturn(ib.Emit(new CallInst(helper, [])));

        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new());
        compilation.EnsureReferencedMembersAccessible(body);
        var attribute = Assert.Single(body.Definition.Module.GetCustomAttribs(forAssembly: true)
            .Where(attribute => attribute.Type.Name == "IgnoresAccessChecksToAttribute"));
        Assert.Equal(library.AsmName.Name, attribute.Args[0]);
    }

    [Fact]
    public void ConstrainedReferenceCallsRetainTheirReceiverIndirection()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var node = body.Definition.Module.CreateType("CilicaTests", "Node");
        var field = node.CreateField("Value", PrimType.Int32, FieldAttributes.Public);
        var reader = node.CreateMethod("Read", PrimType.Int32, [new ParamDef(node, "this")], MethodAttributes.Public);
        reader.Body = new MethodBody(reader);
        var rb = new IRBuilder(reader.Body.CreateBlock());
        rb.CreateReturn(rb.CreateFieldLoad(field, reader.Body.Args[0]));
        var caller = body.Definition.DeclaringType.CreateMethod("ReadNode", PrimType.Int32,
            [new ParamDef(node.CreateByref(), "node")], MethodAttributes.Public | MethodAttributes.Static);
        caller.Body = new MethodBody(caller);
        var ib = new IRBuilder(caller.Body.CreateBlock());
        var call = ib.Emit(new CallInst(reader, [caller.Body.Args[0]], isVirtual: true, constraint: node));
        ib.CreateReturn(call);

        Run(caller.Body, new InlineMethods());
        Assert.DoesNotContain(call, caller.Body.Instructions());
        var receiver = Assert.Single(caller.Body.Instructions().OfType<LoadInst>(), load => load.Address == caller.Body.Args[0]);
        Assert.Equal(node, receiver.ResultType);
        var access = Assert.Single(caller.Body.Instructions().OfType<FieldAddrInst>());
        Assert.Same(receiver, access.Obj);
        Assert.False(access.InBounds);
        var root = new IRBuilder(body.CreateBlock());
        var value = body.CreateVar(node);
        root.CreateInitObj(value);
        root.CreateReturn(root.CreateCall(caller, value));
        Assert.Throws<NullReferenceException>(() => Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CoreLibInliningPreservesTypeAccessBoundaries(bool corelib, bool sameType)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = corelib ? resolver.CoreLib : resolver.Create("AccessBoundaries");
        var callerType = module.CreateType("CilicaTests", "Caller");
        var calleeType = sameType ? callerType : module.CreateType("CilicaTests", "Callee");
        var field = calleeType.CreateField("Value", PrimType.Int32, FieldAttributes.Private | FieldAttributes.Static);
        var callee = calleeType.CreateMethod("Read", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        var reader = new IRBuilder(callee.Body.CreateBlock());
        reader.CreateReturn(reader.CreateFieldLoad(field));
        var caller = callerType.CreateMethod("Run", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        caller.Body = new MethodBody(caller);
        var builder = new IRBuilder(caller.Body.CreateBlock());
        var call = builder.CreateCall(callee);
        builder.CreateReturn(call);
        var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
        PassRunner.Run(new InlineMethods(), new MethodTransformContext(compilation, caller.Body));
        compilation.EnsureReferencedMembersAccessible(caller.Body);

        Assert.Equal(corelib && !sameType, call.Block != null);
        Assert.Equal(!corelib && !sameType, module.GetCustomAttribs(forAssembly: true)
            .Any(attribute => attribute.Type.Name == "IgnoresAccessChecksToAttribute"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void PruningConstantBranchesMaintainsFinallyExits(bool takeLeft, bool inline)
    {
        var action = _modResolver.Import(typeof(Action));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, action);
        var callee = body.Definition.DeclaringType.CreateMethod("WithFinally", PrimType.Int32,
            [new ParamDef(PrimType.Bool, "takeLeft"), new ParamDef(action, "cleanup")],
            MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        var entry = callee.Body.CreateBlock();
        var guarded = callee.Body.CreateBlock();
        var left = callee.Body.CreateBlock();
        var right = callee.Body.CreateBlock();
        var handler = callee.Body.CreateBlock();
        var exitLeft = callee.Body.CreateBlock();
        var exitRight = callee.Body.CreateBlock();
        var exit = callee.Body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Finally, handler));
        guarded.SetBranch(new BranchInst(callee.Body.Args[0], left, right));
        left.SetBranch(new LeaveInst(exitLeft));
        right.SetBranch(new LeaveInst(exitRight));
        new IRBuilder(handler).CreateCallVirt(action.FindMethod("Invoke"), callee.Body.Args[1]);
        handler.SetBranch(new ResumeInst([exitLeft, exitRight]));
        exitLeft.SetBranch(exit);
        exitRight.SetBranch(exit);
        var result = exit.InsertPhi(new PhiInst(PrimType.Int32,
            (exitLeft, ConstInt.CreateI(17)), (exitRight, ConstInt.CreateI(29))));
        exit.SetBranch(new ReturnInst(result));
        IRVerifier.Verify(callee.Body);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(callee, ConstInt.CreateI(takeLeft ? 1 : 0), body.Args[0]));

        MethodBody transformed;
        if (inline) {
            Run(body, new InlineMethods());
            Assert.DoesNotContain(body.Instructions().OfType<CallInst>(), call => call.Method == callee);
            transformed = body;
        } else {
            callee.Body.Args[0].ReplaceUses(ConstInt.CreateI(takeLeft ? 1 : 0));
            Run(callee.Body, new DeadCodeElim());
            transformed = callee.Body;
        }
        Assert.Single(Assert.Single(transformed.Instructions().OfType<ResumeInst>()).GetExitTargets());
        var run = Utils.Compile<Func<Action, int>>(body);
        int cleanups = 0;
        Assert.Equal(takeLeft ? 17 : 29, run(() => cleanups++));
        Assert.Equal(1, cleanups);
        var error = new InvalidOperationException();
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => run(() => throw error)));
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
    public void InliningPreservesBaseConstructorTypeInitialization(bool genericBase, bool throwingInitializer)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var module = body.Definition.Module;
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var baseDefinition = module.CreateType(null, genericBase ? "Base`1" : "Base",
            genericParams: genericBase ? [new GenericParamType(0, false)] : []);
        TypeDefOrSpec baseType = genericBase ? baseDefinition.GetSpec([PrimType.Int32]) : baseDefinition;
        var cctor = baseDefinition.CreateMethod(".cctor", PrimType.Void, [],
            MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        cctor.Body = new MethodBody(cctor);
        var init = new IRBuilder(cctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        if (throwingInitializer) {
            init.Emit(new ThrowInst(init.CreateNewObj(_modResolver.Import(typeof(InvalidOperationException)).FindMethod(".ctor", new MethodSig(PrimType.Void, [])))));
        } else {
            init.CreateReturn();
        }
        var baseCtor = baseDefinition.CreateMethod(".ctor", PrimType.Void, [new ParamDef(baseDefinition.GetUnboundSpec(), "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        baseCtor.Body = new MethodBody(baseCtor);
        var baseBuilder = new IRBuilder(baseCtor.Body.CreateBlock());
        baseBuilder.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), baseCtor.Body.Args[0]);
        baseBuilder.CreateReturn();
        var derived = module.CreateType(null, "Derived", baseType: baseType);
        var ctor = derived.CreateMethod(".ctor", PrimType.Void, [new ParamDef(derived, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var constructor = new IRBuilder(ctor.Body.CreateBlock());
        var call = constructor.CreateCall(baseType.FindMethod(".ctor"), ctor.Body.Args[0]);
        constructor.CreateReturn();
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNewObj(ctor);
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        CheckBehavior(Utils.Compile<Func<int>>(body));
        Run(ctor.Body, new InlineMethods(), new DeadCodeElim());
        Assert.Null(call.Block);
        CheckBehavior(Utils.Compile<Func<int>>(body));

        void CheckBehavior(Func<int> run)
        {
            if (throwingInitializer) {
                Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(() => run()).InnerException);
            } else {
                Assert.Equal(50, run());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EliminatingValueTypeCallsPreservesTypeInitialization(bool inline)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var type = body.Definition.Module.CreateType(null, "Value", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            baseType: (TypeDefOrSpec)_modResolver.Import(typeof(ValueType)));
        var cctor = type.CreateMethod(".cctor", PrimType.Void, [],
            MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        cctor.Body = new MethodBody(cctor);
        var init = new IRBuilder(cctor.Body.CreateBlock());
        init.CreateFieldStore(effect, null, ConstInt.CreateI(50));
        init.CreateReturn();
        var method = type.CreateMethod("Noop", PrimType.Void, [new ParamDef(type.CreateByref(), "this")], MethodAttributes.Public);
        method.Body = new MethodBody(method);
        method.Body.CreateBlock().SetBranch(new ReturnInst());
        var builder = new IRBuilder(body.CreateBlock());
        var value = body.CreateVar(type);
        builder.CreateInitObj(value);
        builder.CreateCall(method, value);
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
        Run(body, inline ? new InlineMethods() : new DeadCodeElim());
        Assert.Equal(50, Utils.Compile<Func<int>>(body)());
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
}
