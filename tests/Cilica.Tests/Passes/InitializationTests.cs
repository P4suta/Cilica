namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Runtime.Loader;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class InitializationTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalTypeInitializationUsesAnEmptyMethodOnTheExactType(bool generic)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.Module.CreateType(null, generic ? "Initialized`1" : "Initialized",
            genericParams: generic ? [new GenericParamType(0, false)] : []);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(type, effect, 50);
        var callee = Constant(type);
        var builder = new IRBuilder(body.CreateBlock());
        TypeDefOrSpec first = generic ? type.GetSpec([PrimType.Int32]) : type;
        TypeDefOrSpec second = generic ? type.GetSpec([PrimType.String]) : type;
        builder.CreateCall(first.FindMethod(callee.Name));
        builder.CreateCall(second.FindMethod(callee.Name));
        builder.CreateCall(first.FindMethod(callee.Name));
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        body = Optimize(body);
        var calls = EmittedCalls(body).ToArray();
        Assert.Equal(generic ? 2 : 1, calls.Length);
        Assert.Equal(first, calls[0].DeclaringType);
        Assert.Equal(second, calls[^1].DeclaringType);
        foreach (var call in calls) AssertEmptyHelper(call);
        Assert.Same(((MethodDefOrSpec)calls[0]).Definition, ((MethodDefOrSpec)calls[^1]).Definition);
        CheckExecution(body, run => Assert.Equal(generic ? 100 : 50, run()));

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, body.Definition.Module.ModName);
        body.Definition.Module.Save(path, savePdb: false);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var reloaded = resolver.Load(path);
        var entry = (MethodDef)reloaded.FindType(null, "TestMethods")!.FindMethod("Run");
        int methodCount = reloaded.MethodDefs().Count();
        var compilation = new Compilation(reloaded, new VoidLogger(), new() { VerifyEachPass = true });
        Assert.True(CompilationPipeline.CreateDefault(compilation).Run([entry]).Succeeded);
        Assert.Equal(methodCount, reloaded.MethodDefs().Count());
        Assert.All(EmittedCalls(entry.Body!), AssertEmptyHelper);
        CheckExecution(entry.Body!, run => Assert.Equal(generic ? 100 : 50, run()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void RedundantInitializationRequiresADominatingCheck(bool dominates, bool external, bool moduleInitializer)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool);
        var module = external ? _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N")) : body.Definition.Module;
        var type = module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(moduleInitializer ? module.FindType(null, "<Module>")! : type, effect, 50);
        var callee = Constant(type);
        var entry = body.CreateBlock();
        var thenBlock = body.CreateBlock();
        var elseBlock = body.CreateBlock();
        var merge = body.CreateBlock();
        var begin = new IRBuilder(entry);
        if (dominates) begin.CreateCall(callee);
        begin.SetBranch(body.Args[0], thenBlock, elseBlock);
        var then = new IRBuilder(thenBlock);
        then.CreateCall(callee);
        then.CreateFieldStore(effect, null, then.CreateAdd(then.CreateFieldLoad(effect), ConstInt.CreateI(1)));
        then.SetBranch(merge);
        var otherwise = new IRBuilder(elseBlock);
        otherwise.CreateFieldStore(effect, null, otherwise.CreateAdd(otherwise.CreateFieldLoad(effect), ConstInt.CreateI(2)));
        otherwise.SetBranch(merge);
        var end = new IRBuilder(merge);
        end.CreateCall(callee);
        end.CreateReturn(end.CreateFieldLoad(effect));

        body = Optimize(body);
        Assert.Equal(dominates ? 1 : 2, EmittedCalls(body).Count(method => method.ReturnType == PrimType.Void));
        if (moduleInitializer) Assert.Equal(dominates ? 1 : 2, EmittedCalls(body).Count(method => method.Name == nameof(RuntimeTypeHandle.GetModuleHandle)));
        CheckExecution<Func<bool, int>>(body, run => Assert.Equal(51, run(true)), external ? [module] : []);
        CheckExecution<Func<bool, int>>(body, run => Assert.Equal(52, run(false)), external ? [module] : []);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void FailedInitializationDoesNotEliminateChecksInHandlersOrContinuations(bool retryInHandler, bool external, bool moduleInitializer)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var module = external ? _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N")) : body.Definition.Module;
        var type = module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(moduleInitializer ? module.FindType(null, "<Module>")! : type, effect, 50, throwing: true);
        var callee = Constant(type);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(TypeInitializationException))));
        var attempt = new IRBuilder(guarded, InsertionDir.After);
        attempt.CreateCall(callee);
        guarded.SetBranch(new LeaveInst(exit));
        var caught = new IRBuilder(handler);
        if (retryInHandler) caught.CreateCall(callee);
        handler.SetBranch(new LeaveInst(exit));
        var continuation = new IRBuilder(exit);
        if (!retryInHandler) continuation.CreateCall(callee);
        continuation.CreateReturn(continuation.CreateFieldLoad(effect));

        body = Optimize(body);
        Assert.Equal(2, EmittedCalls(body).Count(method => method.ReturnType == PrimType.Void));
        if (moduleInitializer) Assert.Equal(2, EmittedCalls(body).Count(method => method.Name == nameof(RuntimeTypeHandle.GetModuleHandle)));
        CheckExecution(body, run => Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(() => run()).InnerException),
            external ? [module] : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeModuleInitializationIsSharedAcrossTypesAndInstantiations(bool separateModules)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var library = _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N"));
        var otherLibrary = separateModules ? _modResolver.Create("OtherLibrary_" + Guid.NewGuid().ToString("N")) : library;
        Initialize(library.FindType(null, "<Module>")!, effect, 10);
        if (separateModules) Initialize(otherLibrary.FindType(null, "<Module>")!, effect, 20);
        var type = library.CreateType(null, "Initialized`1", genericParams: [new GenericParamType(0, false)]);
        var otherType = otherLibrary.CreateType(null, "OtherInitialized");
        Initialize(type, effect, 50);
        Initialize(otherType, effect, 100);
        var callee = Constant(type);
        var otherCallee = Constant(otherType);
        var wrapperType = body.Definition.Module.CreateType(null, "Consumer`1", genericParams: [new GenericParamType(0, false)]);
        var wrapper = Constant(wrapperType);
        wrapper.Body = new MethodBody(wrapper);
        var invoke = new IRBuilder(wrapper.Body.CreateBlock());
        invoke.CreateReturn(invoke.CreateCall(type.GetSpec([.. wrapperType.GenericParams]).FindMethod(callee.Name)));
        Optimize(wrapper.Body);

        var builder = new IRBuilder(body.CreateBlock());
        for (int i = 0; i < 8; i++) {
            builder.CreateCall(wrapperType.GetSpec([PrimType.Int32]).FindMethod(wrapper.Name));
            builder.CreateCall(otherCallee);
            builder.CreateCall(wrapperType.GetSpec([PrimType.String]).FindMethod(wrapper.Name));
            builder.CreateFieldStore(effect, null, builder.CreateAdd(builder.CreateFieldLoad(effect), ConstInt.CreateI(1)));
        }
        builder.CreateReturn(builder.CreateFieldLoad(effect));
        body = Optimize(body);

        var calls = EmittedCalls(body).ToArray();
        Assert.DoesNotContain(calls, method => method.Name == "Value");
        Assert.Equal(separateModules ? 2 : 1, calls.Count(method => method.Name == nameof(RuntimeTypeHandle.GetModuleHandle)));
        Assert.Equal(separateModules ? 2 : 1, calls.Count(method => method.Name == "RunModuleConstructor"));
        Assert.Equal(3, calls.Count(method => method.Name == "RunClassConstructor"));
        CheckExecution(body, run => Assert.Equal(separateModules ? 238 : 218, run()), separateModules ? [library, otherLibrary] : [library]);
    }

    [Fact]
    public void InliningSizeBudgetIncludesRuntimeInitializationCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var target = Constant(body.Definition.Module.CreateType(null, "Consumer"));
        var builder = new IRBuilder(target.Body!.EntryBlock, InsertionDir.BeforeLast);
        for (int i = 0; i < 16; i++) builder.CreateModuleInitialization(_modResolver.Import(typeof(object)));
        var caller = new IRBuilder(body.CreateBlock());
        caller.CreateReturn(caller.CreateCall(target));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { MaxInlinedMethodILSize = 350 });

        PassRunner.Run(new InlineMethods(), new MethodTransformContext(compilation, body));

        Assert.InRange(ILGenerator.GenerateCode(body).GetCodeSize(), 1, 350);
        CheckExecution(body, run => Assert.Equal(42, run()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypeInitializationPreservesGenericDefinitionTokens(bool constructed, bool staticField)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var type = body.Definition.Module.CreateType(null, "Initialized`1", genericParams: [new GenericParamType(0, false)]);
        if (staticField) type.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(type, effect, 50);
        var wrapperType = body.Definition.Module.CreateType(null, "Consumer`1", genericParams: [new GenericParamType(0, false)]);
        var wrapper = Constant(wrapperType);
        var initialize = new IRBuilder(wrapper.Body!.EntryBlock, InsertionDir.BeforeLast);
        initialize.CreateTypeInitialization(constructed ? type.GetSpec([.. wrapperType.GenericParams]) : type);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(wrapperType.GetSpec([PrimType.Int32]).FindMethod(wrapper.Name));
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        body = Optimize(body);

        CheckExecution(body, run => Assert.Equal(constructed ? 50 : 0, run()));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    public void ExternalTypeInitializationCanAvoidRuntimeCalls(bool generic, bool readOnly, bool throwing, bool moduleThrows)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var library = _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N"));
        Initialize(library.FindType(null, "<Module>")!, effect, 10, throwing: moduleThrows);
        var type = library.CreateType(null, generic ? "Initialized`1" : "Initialized",
            genericParams: generic ? [new GenericParamType(0, false)] : []);
        type.CreateField("Trigger", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static | (readOnly ? FieldAttributes.InitOnly : 0));
        Initialize(type, effect, 50, throwing);
        var callee = Constant(type);
        if (generic) {
            var wrapperType = body.Definition.Module.CreateType(null, "Consumer`1", genericParams: [new GenericParamType(0, false)]);
            var wrapper = Constant(wrapperType);
            wrapper.Body = new MethodBody(wrapper);
            var invoke = new IRBuilder(wrapper.Body.CreateBlock());
            invoke.CreateReturn(invoke.CreateCall(type.GetSpec([.. wrapperType.GenericParams]).FindMethod(callee.Name)));
            Optimize(wrapper.Body);
            type = wrapperType;
            callee = wrapper;
        }
        TypeDefOrSpec first = generic ? type.GetSpec([PrimType.Int32]) : type;
        TypeDefOrSpec second = generic ? type.GetSpec([PrimType.String]) : type;
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(first.FindMethod(callee.Name));
        builder.CreateCall(second.FindMethod(callee.Name));
        builder.CreateCall(first.FindMethod(callee.Name));
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        body = Optimize(body);

        Assert.Empty(EmittedCalls(body));
        CheckExecution(body, run => {
            for (int i = 0; i < 2; i++) {
                if (throwing || moduleThrows) Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(() => run()).InnerException);
                else Assert.Equal(generic ? 110 : 60, run());
            }
        }, library);
    }

    [Theory]
    [InlineData(FieldAttributes.Private | FieldAttributes.Static, false)]
    [InlineData(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, false)]
    [InlineData(FieldAttributes.Public | FieldAttributes.Static, true)]
    public void TypeInitializationDoesNotRequireAccessToIneligibleFields(FieldAttributes attributes, bool threadStatic)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var library = _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N"));
        var type = library.CreateType(null, "Initialized");
        var field = type.CreateField("Value", PrimType.Int32, attributes,
            defaultValue: attributes.HasFlag(FieldAttributes.Literal) ? 0 : null);
        if (threadStatic) field.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(_modResolver.Import(typeof(ThreadStaticAttribute)).FindMethod(".ctor")));
        Initialize(type, effect, 50);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(Constant(type));
        builder.CreateReturn(builder.CreateFieldLoad(effect));

        body = Optimize(body);

        Assert.Single(EmittedCalls(body), method => method.Name == "RunClassConstructor");
        CheckExecution(body, run => Assert.Equal(50, run()), library);
    }

    [Fact]
    public void ModuleInitializationUsesAGenericDefinitionHandle()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var library = _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N"));
        Initialize(library.FindType(null, "<Module>")!, effect, 10);
        var type = library.CreateType(null, "Initialized`1", genericParams: [new GenericParamType(0, false)]);
        Initialize(type, effect, 50, throwing: true);
        var wrapperType = body.Definition.Module.CreateType(null, "Consumer`1", genericParams: [new GenericParamType(0, false)]);
        var wrapper = Constant(wrapperType);
        var initialize = new IRBuilder(wrapper.Body!.EntryBlock, InsertionDir.BeforeLast);
        initialize.CreateModuleInitialization(type.GetSpec([.. wrapperType.GenericParams]));
        Optimize(wrapper.Body);

        var token = Assert.Single(wrapper.ILBody!.Instructions.Where(inst => inst.OpCode == ILCode.Ldtoken));
        Assert.Same(type, token.Operand);

        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(wrapperType.GetSpec([PrimType.String]).FindMethod(wrapper.Name));
        builder.CreateReturn(builder.CreateFieldLoad(effect));
        body = Optimize(body);

        CheckExecution(body, run => Assert.Equal(10, run()), library);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeInitializationCoalescesEquivalentTypeTokens(bool moduleInitializer)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        for (int i = 0; i < 2; i++) {
            var type = new ArrayType(PrimType.Int32);
            if (moduleInitializer) builder.CreateModuleInitialization(type);
            else builder.CreateTypeInitialization(type);
        }
        builder.CreateReturn(ConstInt.CreateI(42));

        body = Optimize(body);

        Assert.Single(EmittedCalls(body), method => method.Name == (moduleInitializer ? "RunModuleConstructor" : "RunClassConstructor"));
        CheckExecution(body, run => Assert.Equal(42, run()));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void CallerInitializationOnlyCoversItsExactTypeInstantiation(bool generic, bool sameInstantiation)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.Module.CreateType(null, generic ? "Initialized`1" : "Initialized",
            genericParams: generic ? [new GenericParamType(0, false)] : []);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(type, effect, 50);
        Constant(type);
        var caller = type.CreateMethod("Invoke", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        caller.Body = new MethodBody(caller);
        var invoke = new IRBuilder(caller.Body.CreateBlock());
        var target = sameInstantiation ? type.GetUnboundSpec() : type.GetSpec([PrimType.Int32]);
        invoke.CreateCall(target.FindMethod("Value"));
        invoke.CreateReturn();
        Optimize(caller.Body);
        if (sameInstantiation) Assert.Empty(EmittedCalls(caller.Body!));
        else Assert.Equal(target, Assert.Single(EmittedCalls(caller.Body!)).DeclaringType);

        TypeDefOrSpec entryType = generic ? type.GetSpec([PrimType.String]) : type;
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(entryType.FindMethod("Invoke"));
        builder.CreateReturn(builder.CreateFieldLoad(effect));
        CheckExecution(body, run => Assert.Equal(sameInstantiation ? 50 : 100, run()));
    }

    [Fact]
    public void ScalarReplacementPreservesInitializationWithoutARuntimeApiCall()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.Module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(type, effect, 50);
        var field = type.CreateField("Value", PrimType.Int32);
        var ctor = type.CreateMethod(".ctor", PrimType.Void, [new ParamDef(type, "this")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        ctor.Body = new MethodBody(ctor);
        var construct = new IRBuilder(ctor.Body.CreateBlock());
        construct.CreateCall(_modResolver.Import(typeof(object)).FindMethod(".ctor"), ctor.Body.Args[0]);
        construct.CreateFieldStore(field, ctor.Body.Args[0], ConstInt.CreateI(42));
        construct.CreateReturn();
        var builder = new IRBuilder(body.CreateBlock());
        var allocation = builder.CreateNewObj(ctor);
        builder.CreateReturn(builder.CreateAdd(builder.CreateFieldLoad(field, allocation), builder.CreateFieldLoad(effect)));

        body = Optimize(body);
        Assert.DoesNotContain(body.Instructions(), inst => inst is NewObjInst);
        AssertEmptyHelper(Assert.Single(EmittedCalls(body)));
        CheckExecution(body, run => Assert.Equal(92, run()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CrossModuleInliningUsesPublishedHelpersOrRuntimeCalls(bool publishHelpers, bool initializeType, bool throwing)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var library = _modResolver.Create("InitializationLibrary_" + Guid.NewGuid().ToString("N"));
        Initialize(library.FindType(null, "<Module>")!, effect, 10, throwing && !initializeType);
        var type = library.CreateType(null, "Initialized");
        if (initializeType) Initialize(type, effect, 50, throwing);
        var callee = Constant(type);

        if (publishHelpers) {
            var probe = Constant(library.CreateType(null, "Consumer"));
            probe.Body = new MethodBody(probe);
            var invoke = new IRBuilder(probe.Body.CreateBlock());
            invoke.CreateReturn(invoke.CreateCall(callee));
            Optimize(probe.Body);
        }
        int libraryMethods = library.MethodDefs().Count();
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(callee);
        builder.CreateReturn(builder.CreateAdd(builder.CreateCall(callee), builder.CreateFieldLoad(effect)));
        body = Optimize(body);

        Assert.Equal(libraryMethods, library.MethodDefs().Count());
        var calls = EmittedCalls(body).ToArray();
        Assert.DoesNotContain(calls, method => method == callee);
        if (publishHelpers) {
            var helper = Assert.Single(calls);
            AssertEmptyHelper(helper);
            Assert.Same(library, ((MethodDefOrSpec)helper).Module);
            Assert.Equal(initializeType, helper.DeclaringType == type);
        } else {
            Assert.Single(calls, method => method.Name == nameof(RuntimeTypeHandle.GetModuleHandle));
            Assert.Single(calls, method => method.Name == "RunModuleConstructor");
            Assert.Equal(initializeType ? 1 : 0, calls.Count(method => method.Name == "RunClassConstructor"));
        }
        CheckExecution(body, run => {
            for (int i = 0; i < 2; i++) {
                if (throwing) {
                    Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(() => run()).InnerException);
                } else {
                    Assert.Equal(initializeType ? 102 : 52, run());
                }
            }
        }, library);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void ReentrantInitializationCannotCacheSuccessBeforeTheInitializerThrows(bool moduleInitializer, bool publishHelpers, bool staticField)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        body.Definition.ImplAttribs = MethodImplAttributes.NoInlining;
        var library = moduleInitializer || staticField ? _modResolver.Create("Reentrant_" + Guid.NewGuid().ToString("N")) : body.Definition.Module;
        var type = library.CreateType(null, "Initialized");
        if (staticField) type.CreateField("Trigger", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var callee = Constant(type);
        var initializerOwner = moduleInitializer ? library.FindType(null, "<Module>")! : type;
        var initializer = CreateInitializer(initializerOwner);
        var initialize = new IRBuilder(initializer.Body!.CreateBlock());
        initialize.CreateCall(body.Definition);
        Throw(initialize);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(callee));
        var start = body.Definition.DeclaringType.CreateMethod("Start", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        start.Body = new MethodBody(start);
        var begin = new IRBuilder(start.Body.CreateBlock());
        if (moduleInitializer) begin.CreateModuleInitialization(type);
        else begin.CreateTypeInitialization(type);
        begin.CreateReturn();
        if (publishHelpers) Optimize(initializer.Body);
        body = Optimize(body);

        CheckExecution(body, run => {
            var start = run.Method.DeclaringType!.GetMethod("Start")!.CreateDelegate<Action>();
            Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(start).InnerException);
            for (int i = 0; i < 2; i++) {
                Assert.IsType<InvalidOperationException>(Assert.Throws<TypeInitializationException>(() => run()).InnerException);
            }
        }, moduleInitializer || staticField ? [library] : []);
    }

    [Fact]
    public void FailedInliningDoesNotLeaveInitializationHelpersBehind()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.Module.CreateType(null, "Initialized");
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        Initialize(type, effect, 50);
        var callee = Constant(type);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateCall(callee);
        builder.CreateReturn(builder.CreateFieldLoad(effect));
        int methodCount = type.Methods.Count;
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new());
        var manager = new PassManager { Compilation = compilation };
        manager.AddPasses().Apply<InlineMethods>().Apply(new FailPass());
        Assert.False(manager.Run([body.Definition]).Succeeded);
        Assert.Equal(methodCount, type.Methods.Count);

        Assert.True(CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).Succeeded);
        body = body.Definition.Body!;
        AssertEmptyHelper(Assert.Single(EmittedCalls(body)));
        CheckExecution(body, run => Assert.Equal(50, run()));
    }

    private static MethodBody Optimize(MethodBody body)
    {
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        var result = CompilationPipeline.CreateDefault(compilation).Run([body.Definition]);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Methods.Select(method => method.Error)));
        return body.Definition.Body!;
    }

    private static MethodDef Constant(TypeDef type)
    {
        var method = type.CreateMethod("Value", PrimType.Int32, [], MethodAttributes.Public | MethodAttributes.Static);
        method.Body = new MethodBody(method);
        method.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        return method;
    }

    private static MethodDef CreateInitializer(TypeDef type)
    {
        var method = type.CreateMethod(".cctor", PrimType.Void, [],
            MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        method.Body = new MethodBody(method);
        return method;
    }

    private void Initialize(TypeDef type, FieldDef effect, int increment, bool throwing = false)
    {
        var initializer = CreateInitializer(type);
        var builder = new IRBuilder(initializer.Body!.CreateBlock());
        builder.CreateFieldStore(effect, null, builder.CreateAdd(builder.CreateFieldLoad(effect), ConstInt.CreateI(increment)));
        if (throwing) Throw(builder);
        else builder.CreateReturn();
    }

    private void Throw(IRBuilder builder)
        => builder.Emit(new ThrowInst(builder.CreateNewObj(_modResolver.Import(typeof(InvalidOperationException))
            .FindMethod(".ctor", new MethodSig(PrimType.Void, [])))));

    private static IEnumerable<MethodDesc> EmittedCalls(MethodBody body)
        => body.Definition.ILBody!.Instructions.Where(inst => inst.OpCode is ILCode.Call or ILCode.Callvirt)
            .Select(inst => (MethodDesc)inst.Operand!);

    private static void AssertEmptyHelper(MethodDesc method)
    {
        var helper = Assert.IsAssignableFrom<MethodDefOrSpec>(method);
        Assert.True(helper.IsStatic);
        Assert.Equal(PrimType.Void, helper.ReturnType);
        Assert.Empty(helper.ParamSig);
        Assert.Equal(ILCode.Ret, Assert.Single(helper.Definition.ILBody!.Instructions).OpCode);
        Assert.False(helper.ImplAttribs.HasFlag(MethodImplAttributes.NoInlining));
    }

    private static void CheckExecution(MethodBody body, Action<Func<int>> check, params ModuleDef[] dependencies)
        => CheckExecution<Func<int>>(body, check, dependencies);

    private static void CheckExecution<T>(MethodBody body, Action<T> check, params ModuleDef[] dependencies) where T : Delegate
    {
        var images = dependencies.Append(body.Definition.Module).ToDictionary(module => module.AsmName.Name!, module => {
            foreach (var method in module.MethodDefs()) {
                if (method.Body != null) method.ILBody = ILGenerator.GenerateCode(method.Body);
            }
            using var stream = new MemoryStream();
            module.Save(stream, null, module.ModName);
            return stream.ToArray();
        });
        var context = new AssemblyLoadContext(null, isCollectible: true);
        context.Resolving += (_, name) => images.TryGetValue(name.Name!, out var bytes)
            ? context.LoadFromStream(new MemoryStream(bytes)) : null;
        try {
            var assembly = context.LoadFromStream(new MemoryStream(images[body.Definition.Module.AsmName.Name!]));
            var entry = assembly.GetType(body.Definition.DeclaringType.Name)!.GetMethod(body.Definition.Name)!;
            check(entry.CreateDelegate<T>());
        } finally {
            context.Unload();
        }
    }

    sealed class FailPass : IMethodPass
    {
        public MethodPassResult Run(MethodTransformContext context) => throw new NotSupportedException("Test failure");
    }
}
