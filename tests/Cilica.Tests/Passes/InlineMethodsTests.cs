namespace Cilica.Tests.Passes;

using System.Reflection;
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
        Assert.Contains(call, caller.Body.Instructions());
        Assert.Same(node, call.Constraint);
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
