namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.Passes;

public partial class FoldReflectionTests
{
    [Theory]
    [InlineData("Fields", 42)]
    [InlineData("StaticFields", "updated")]
    [InlineData("ObjectFieldStoresMissing", true)]
    [InlineData("StructFieldLoad", 42)]
    [InlineData("FieldBoxIdentity", true)]
    [InlineData("Properties", 42)]
    [InlineData("StaticProperties", 17)]
    [InlineData("MethodArguments", 42)]
    [InlineData("VoidInvocation", true)]
    [InlineData("VirtualInvocation", 42)]
    [InlineData("BoxedStructFields", 42)]
    [InlineData("BoxedStructMethod", 1)]
    [InlineData("MutatedBoxArgument", 42)]
    [InlineData("ClosedGeneric", 42)]
    [InlineData("ExactGetType", 7)]
    [InlineData("ChangedArguments", 42)]
    [InlineData("EmptyArguments", 42)]
    [InlineData("ReferenceArguments", "hello")]
    [InlineData("NullReferenceArgument", true)]
    [InlineData("NullableResult", 42)]
    [InlineData("DeclaredNonPublicField", 42)]
    [InlineData("DeclaredOverride", 42)]
    [InlineData("ReflectionExceptionFilter", 111)]
    [InlineData("WrappedException", "InvalidOperationException")]
    [InlineData("UnwrappedException", "failure")]
    [InlineData("PropertyException", "InvalidOperationException")]
    [InlineData("InvocationInsideFinally", 52)]
    [InlineData("UnwrappedInvocationInsideFilter", 42)]
    [InlineData("InitializedField", 42)]
    [InlineData("InitializedMethod", 42)]
    [InlineData("ThrowingInitializer", "TypeInitializationException")]
    public void KnownAccessesUseDirectOperationsAndPreserveBehavior(string name, object expected)
    {
        using var test = new Case(name);
        Assert.Equal(expected, test.Original.Invoke(null, null));

        test.Optimize();

        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc method &&
            method.DeclaringType.IsCorelibType(typeof(Type)) && method.Name is "GetField" or "GetMethod" or "GetProperty");
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("EscapedArguments", 42)]
    [InlineData("ArgumentsUsedAfterInvocation", 43)]
    [InlineData("PrimitiveConversion", 42L)]
    [InlineData("NullValueArgument", 0)]
    [InlineData("WrongArgumentCount", "TargetParameterCountException")]
    [InlineData("InvalidFieldValue", "ArgumentException")]
    [InlineData("IndexedProperty", 42)]
    [InlineData("GenericMethodDefinition", "InvalidOperationException")]
    [InlineData("InaccessibleDirectField", 42)]
    [InlineData("RefArgument", 42)]
    [InlineData("BinderArgument", 42)]
    [InlineData("MissingOptionalArgument", "default")]
    [InlineData("WideFieldStore", 42L)]
    [InlineData("InheritedMember", 9)]
    [InlineData("IgnoreCaseLookup", 7)]
    [InlineData("AmbiguousLookup", "AmbiguousMatchException")]
    [InlineData("MissingMember", "NullReferenceException")]
    [InlineData("ReadonlyStore", "FieldAccessException")]
    [InlineData("OpenGeneric", "InvalidOperationException")]
    [InlineData("InvocationInsideFilter", 42)]
    [InlineData("PropertyInsideFilter", 42)]
    public void UnsupportedAccessesRetainReflectionBehavior(string name, object expected)
    {
        using var test = new Case(name);
        Assert.Equal(expected, test.Original.Invoke(null, null));
        test.Optimize();
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void InvalidReceiversRetainReflectionExceptions()
    {
        using var test = new Case("InvalidReceiver");
        test.Optimize();
        var run = test.Emit();
        foreach (var value in new object?[] { null, new object() }) {
            Assert.Equal(test.Original.Invoke(null, [value]), run.Invoke(null, [value]));
        }
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
    }

    [Fact]
    public void ObjectArgumentsThatCouldBeMissingRetainDefaultArgumentHandling()
    {
        using var test = new Case("OpaqueObjectArgument");
        test.Optimize();
        var run = test.Emit().CreateDelegate<Func<object?, object>>();
        var original = test.Original.CreateDelegate<Func<object?, object>>();
        foreach (var value in new object?[] { null, "hello", Type.Missing }) {
            Assert.Equal(original(value), run(value));
        }
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
    }

    [Fact]
    public void FoldingAStaticAccessPreservesGetTypeReceiverValidation()
    {
        using var test = new Case("StaticLookupThroughNullableTarget");
        test.Optimize();
        var run = test.Emit();
        Assert.IsType<NullReferenceException>(Assert.Throws<TargetInvocationException>(() => run.Invoke(null, [null])).InnerException);
        var receiver = Activator.CreateInstance(run.GetParameters()[0].ParameterType);
        Assert.Equal("initial", run.Invoke(null, [receiver]));
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonExceptionThrowsPreserveReflectionWrapping(bool wrapNonExceptions)
    {
        using var test = new Case("WrappedException");
        test.UseNonExceptionThrow(wrapNonExceptions);
        Assert.Equal("RuntimeWrappedException", test.Emit().Invoke(null, null));
        test.Optimize();
        Assert.Equal("RuntimeWrappedException", test.Emit().Invoke(null, null));
        Assert.Equal(!wrapNonExceptions, test.Method.ILBody!.Instructions.Any(instruction => IsReflectionAccess(instruction.Operand)));
    }

    [Theory]
    [InlineData("InvocationInsideFilter")]
    [InlineData("PropertyInsideFilter")]
    public void DefaultPipelinePreservesWrappingInsideFilters(string name)
    {
        using var test = new Case(name);
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("FieldInitializationMutatesArgument", false)]
    [InlineData("FieldInitializationMutatesArgument", true)]
    [InlineData("MethodInitializationMutatesArgument", false)]
    [InlineData("MethodInitializationMutatesArgument", true)]
    public void TypeInitializationPreservesBoxedArgumentReadOrder(string name, bool defaultPipeline)
    {
        using var test = new Case(name);
        object? expected = test.Original.Invoke(null, null);
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void DiscoveryRemainsWhenItsMetadataIsObserved()
    {
        using var test = new Case("LookupStillUsed");
        test.Optimize();
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Contains(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetField" });
        Assert.Equal("Value:7", test.Emit().Invoke(null, null));
    }

    [Fact]
    public void TrimmingDependencyPolicyRetainsDiscovery()
    {
        using var test = new Case("Fields");
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Contains(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetField" });
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("Fields", 42)]
    [InlineData("MethodArguments", 42)]
    [InlineData("BoxedStructFields", 42)]
    [InlineData("BoxedStructMethod", 1)]
    [InlineData("WrappedException", "InvalidOperationException")]
    [InlineData("Properties", 42)]
    [InlineData("ClosedGeneric", 42)]
    [InlineData("ReflectionExceptionFilter", 111)]
    [InlineData("InvocationInsideFinally", 52)]
    [InlineData("ThrowingInitializer", "TypeInitializationException")]
    [InlineData("FieldBoxIdentity", true)]
    [InlineData("ThroughHelper", 7)]
    [InlineData("UnwrappedInvocationInsideFilter", 42)]
    public void DefaultPipelineFoldsReflectionIncludingInlinedLookups(string name, object expected)
    {
        using var test = new Case(name);
        test.Optimize(defaultPipeline: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionAccess(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    private static bool IsReflectionAccess(object? operand)
        => operand is MethodDesc method && method.DeclaringType.Namespace == "System.Reflection" &&
            method.Name is "GetValue" or "SetValue" or "Invoke";

    private sealed class Case : IDisposable
    {
        readonly ModuleResolver _resolver = new();
        readonly ModuleDef _module;
        readonly string _typeName;
        public MethodDef Method { get; }
        public MethodInfo Original { get; }

        public Case(string name, string typeName = "ReflectionCases")
        {
            _typeName = typeName;
            _resolver.AddTrustedSearchPaths();
            string path = Path.Combine(AppContext.BaseDirectory, "TestAsm.dll");
            _module = _resolver.Load(path);
            Method = (MethodDef)_module.FindType(null, typeName)!.FindMethod(name);
            Original = Assembly.Load(File.ReadAllBytes(path)).GetType(typeName)!.GetMethod(name)!;
        }

        public void Optimize(bool defaultPipeline = false, bool preserveTrimmingDependencies = false)
        {
            var compilation = new Compilation(_module, new VoidLogger(), new() {
                VerifyEachPass = true,
                PreserveTrimmingDependencies = preserveTrimmingDependencies
            });
            var manager = defaultPipeline ? CompilationPipeline.CreateDefault(compilation)
                : new PassManager { Compilation = compilation };
            if (!defaultPipeline) manager.AddPasses().Apply<SimplifyCFG>().Apply<SsaPromotion>().Apply<FoldReflection>().Apply<DeadCodeElim>();
            var result = manager.Run([Method]);
            Assert.True(result.Succeeded, string.Join("\n", result.Methods.Select(method => method.Error?.ToString())));
        }

        public MethodInfo Emit()
        {
            using var output = new MemoryStream();
            _module.Save(output, null, _module.ModName);
            return Assembly.Load(output.ToArray()).GetType(_typeName)!.GetMethod(Method.Name)!;
        }

        public void UseNonExceptionThrow(bool wrap)
        {
            var attributes = _module.GetCustomAttribs(forAssembly: true);
            var compatibility = attributes.Find(typeof(System.Runtime.CompilerServices.RuntimeCompatibilityAttribute))!;
            if (!wrap) attributes.Remove(compatibility);
            var target = (MethodDef)_module.FindType(null, "ReflectionCases")!.NestedTypes.Single(type => type.Name == "Target").FindMethod("Fail");
            target.ILBody = new ILMethodBody {
                MaxStack = 1,
                Instructions = new[] { new ILInstruction(ILCode.Ldstr, "raw"), new ILInstruction(ILCode.Throw) { Offset = 5 } }
            };
        }

        public void AddDelegateTypeInitializer(bool beforeFieldInit)
        {
            var owner = _module.FindType(null, _typeName)!;
            var delegateType = owner.NestedTypes.Single(type => type.Name == "RefAction");
            if (beforeFieldInit) delegateType.Attribs |= TypeAttributes.BeforeFieldInit;
            else delegateType.Attribs &= ~TypeAttributes.BeforeFieldInit;
            var initializer = delegateType.CreateMethod(".cctor", PrimType.Void, [],
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            initializer.Body = new Cilica.IR.MethodBody(initializer);
            var builder = new Cilica.IR.Utils.IRBuilder(initializer.Body.CreateBlock());
            builder.CreateFieldStore(owner.FindField("Result"), null, Cilica.IR.ConstInt.CreateI(42));
            builder.CreateReturn();
            initializer.ILBody = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(initializer.Body);
        }

        public void Dispose() => _resolver.Dispose();
    }
}
