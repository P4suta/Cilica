namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("MethodDelegate", 42)]
    [InlineData("MethodInvoke", 42)]
    [InlineData("MethodPointer", "hello")]
    [InlineData("ConstructedType", 42)]
    [InlineData("ConstructedDelegateType", 42)]
    [InlineData("MethodOnGenericOwner", "String:Object:hello")]
    [InlineData("ClosedStaticGenericReturnBuffer", "String:Object:hello:world")]
    [InlineData("OpenGenericStructDelegate", "String:Object:hello:41")]
    [InlineData("DerivedOverride", 42)]
    [InlineData("InterfaceInvoke", 42)]
    [InlineData("ClosedInterfaceDelegate", 42)]
    [InlineData("AbstractInvoke", 42)]
    [InlineData("ClosedAbstractDelegate", 42)]
    [InlineData("BoxedInterfaceInvoke", 42)]
    [InlineData("BoxedInterfaceDelegate", 42)]
    [InlineData("BoxedGenericVirtualInvoke", "String:Object:hello:41")]
    [InlineData("GenericVirtualDelegate", "String:Object:hello")]
    [InlineData("GenericVirtualInvoke", "String:Object:hello")]
    [InlineData("DefaultInterfaceInvoke", 42)]
    [InlineData("DefaultInterfaceDelegate", 42)]
    [InlineData("BoxedDefaultInterfaceIdentity", true)]
    [InlineData("StructConstraint", 42)]
    [InlineData("ReferenceConstraint", "hello")]
    [InlineData("ConstructorConstraint", 42)]
    [InlineData("InterfaceConstraint", 42)]
    public void GenericAndVirtualReflectionPreservesBehavior(string name, object expected)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            AssertNoGenericReflection(test.Method);
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("GenericBody")]
    [InlineData("GenericOwnerBody")]
    [InlineData("GenericConstructedTypeBody")]
    [InlineData("GenericField")]
    [InlineData("GenericPointer")]
    public void RuntimeTypeParametersKeepTheirGenericContext(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(defaultPipeline: defaultPipeline);
            AssertNoGenericReflection(test.Method);
            var emitted = test.Emit();
            foreach (var (type, value) in new (Type, object?)[] { (typeof(int), 42), (typeof(string), "hello"),
                (typeof(object), new object()), (typeof((int, long)), (7, 12L)), (typeof(int?), null), (typeof(int?), 42) }) {
                var run = emitted.MakeGenericMethod(type);
                Assert.Equal(test.Original.MakeGenericMethod(type).Invoke(null, [value]), run.Invoke(null, [value]));
            }
        }
    }

    [Theory]
    [InlineData("GenericValueInvoke")]
    [InlineData("GenericConstrainedBody")]
    public void RuntimeStructConstraintsAllowGenericBoxing(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        foreach (var (type, value) in new (Type, object)[] { (typeof(int), 42), (typeof(long), 42L), (typeof((int, long)), (7, 12L)) }) {
            Assert.Equal(value, emitted.MakeGenericMethod(type).Invoke(null, [value]));
        }
    }

    [Fact]
    public void BoxedGenericInterfaceReceiversKeepMutations()
    {
        using var test = new Case("GenericBoxedInterface", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var method = test.Emit();
        var counter = method.DeclaringType!.GetNestedType("Counter")!;
        Assert.Equal(42, method.MakeGenericMethod(counter).Invoke(null, [Activator.CreateInstance(counter, [40])]));
    }

    [Theory]
    [InlineData("EscapedBoxedGenericVirtualDelegate")]
    [InlineData("EscapedBoxedGenericMethodDelegate")]
    public void EscapingDelegatesRetainUnboxingAndInstantiationContexts(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            var original = (Func<object, string>)test.Original.Invoke(null, null)!;
            test.Optimize(defaultPipeline: defaultPipeline);
            if (name == "EscapedBoxedGenericMethodDelegate") AssertNoGenericReflection(test.Method);
            var callback = (Func<object, string>)test.Emit().Invoke(null, null)!;
            Assert.Equal(original("hello"), callback("hello"));
            Assert.Equal(original("world"), callback("world"));
            Assert.NotNull(callback.Target);
            Assert.Equal(typeof(object), Assert.Single(callback.Method.GetGenericArguments()));
            Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
            Assert.Equal(typeof(string), Assert.Single(callback.Target!.GetType().GetGenericArguments()));
        }
    }

    [Theory]
    [InlineData("GenericBoxedVirtualFactory")]
    [InlineData("GenericBoxedMethodFactory")]
    public void GenericEscapingFactoriesRetainBothRuntimeInstantiations(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(defaultPipeline: defaultPipeline);
            if (name == "GenericBoxedMethodFactory") AssertNoGenericReflection(test.Method);
            var emitted = test.Emit();
            foreach (var (owner, argument, value) in new (Type, Type, object)[] {
                (typeof(string), typeof(object), "hello"), (typeof(object), typeof(string), "world"),
                (typeof(string), typeof((int, long)), (1, 2L)), (typeof(int), typeof(long), 42L)
            }) {
                var original = (Delegate)test.Original.MakeGenericMethod(owner, argument).Invoke(null, null)!;
                var callback = (Delegate)emitted.MakeGenericMethod(owner, argument).Invoke(null, null)!;
                Assert.Equal(original.DynamicInvoke(value), callback.DynamicInvoke(value));
                Assert.Equal(original.DynamicInvoke(value), callback.DynamicInvoke(value));
                Assert.Equal(argument, Assert.Single(callback.Method.GetGenericArguments()));
                Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
                Assert.Equal(owner, Assert.Single(callback.Target!.GetType().GetGenericArguments()));
            }
        }
    }

    [Fact]
    public void GenericDefaultInterfaceBodiesSeeTheOriginalBox()
    {
        using var test = new Case("GenericDefaultInterfaceIdentity", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        var valueType = emitted.DeclaringType!.GetNestedType("DefaultValue")!;
        Assert.Equal(true, emitted.MakeGenericMethod(valueType).Invoke(null, [Activator.CreateInstance(valueType)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericDefaultInterfaceDelegatesPreserveMethodIdentity(bool defaultPipeline)
    {
        using var test = new Case("EscapedGenericDefaultInterface", "ReflectionGenericCases");
        var original = (Func<object, string>)test.Original.Invoke(null, null)!;
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var callback = (Func<object, string>)test.Emit().Invoke(null, null)!;
        Assert.Equal(original("hello"), callback("hello"));
        Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
        Assert.Equal(original.Method.GetGenericArguments(), callback.Method.GetGenericArguments());
    }

    private delegate ref int RefGetter();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericBoxPayloadReferencesRemainValidAfterReturning(bool defaultPipeline)
    {
        using var test = new Case("EscapingBoxReference", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        foreach (var type in new[] { typeof(string), typeof(int), typeof(long) }) {
            var get = emitted.MakeGenericMethod(type).CreateDelegate<RefGetter>();
            ref int value = ref get();
            Assert.Equal(40 + type.Name.Length, value);
            value = 101;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(101, value);
        }
    }

    [Theory]
    [InlineData("InlinedGenericType")]
    [InlineData("InlinedClosedVirtual")]
    public void InliningExposesReflectionArgumentsAndTargets(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("ConstructedTypeResult", false)]
    [InlineData("ConstructedTypeResult", true)]
    [InlineData("InlinedTypeResult", true)]
    public void TypeConstructionFoldsWhenTheTypeResultEscapes(string name, bool defaultPipeline)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var expected = (Type)test.Original.Invoke(null, null)!;
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var actual = (Type)test.Emit().Invoke(null, null)!;
        Assert.Equal(expected.FullName, actual.FullName);
        Assert.Equal(typeof(int), Assert.Single(actual.GetGenericArguments()));
    }

    [Fact]
    public void TypeConstructionRetainsRequestedTrimmingDependencies()
    {
        using var test = new Case("ConstructedTypeResult", "ReflectionGenericCases");
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => instruction.Operand is MethodDesc { Name: "MakeGenericType" });
        Assert.Equal(typeof(int), Assert.Single(((Type)test.Emit().Invoke(null, null)!).GetGenericArguments()));
    }

    [Theory]
    [InlineData("InvalidStructConstraint")]
    [InlineData("InvalidTypeConstraint")]
    [InlineData("InvalidConstructorConstraint")]
    [InlineData("InvalidInterfaceConstraint")]
    [InlineData("MissingTypeArgument")]
    [InlineData("WrongArity")]
    [InlineData("AlreadyConstructed")]
    [InlineData("OpenInterfaceDelegate")]
    [InlineData("OpenAbstractDelegate")]
    [InlineData("OpenGenericVirtualDelegate")]
    [InlineData("ChangedTypeArguments")]
    public void UnprovenGenericConstructionsRetainRuntimeBehavior(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var expected = test.Original.Invoke(null, null);
        test.Optimize();
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("OpaqueGenericInvoke")]
    [InlineData("GenericUnknownConstraint")]
    public void UnknownGenericValuesKeepInvocationValidation(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        var emitted = test.Emit();
        foreach (var (type, value) in new (Type, object)[] { (typeof(int), 42), (typeof(string), "hello") }) {
            Assert.Equal(test.Original.MakeGenericMethod(type).Invoke(null, [value]), emitted.MakeGenericMethod(type).Invoke(null, [value]));
        }
        if (name == "OpaqueGenericInvoke") {
            var original = test.Original.MakeGenericMethod(typeof(object)).CreateDelegate<Func<object, object>>();
            var run = emitted.MakeGenericMethod(typeof(object)).CreateDelegate<Func<object, object>>();
            Assert.Equal(original(Type.Missing), run(Type.Missing));
        }
    }

    [Fact]
    public void IndirectClassConstraintsDoNotExcludeBoxedValueTypes()
    {
        using var test = new Case("IndirectReferenceConstraint", "ReflectionGenericCases");
        Assert.Equal("ArgumentException", test.Original.MakeGenericMethod(typeof(int), typeof(object)).Invoke(null, [42]));
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        var emitted = test.Emit();
        Assert.Equal("ArgumentException", emitted.MakeGenericMethod(typeof(int), typeof(object)).Invoke(null, [42]));
        Assert.Equal("hello", emitted.MakeGenericMethod(typeof(string), typeof(object)).Invoke(null, ["hello"]));
    }

    [Theory]
    [InlineData("MethodDelegate", "MakeGenericMethod")]
    [InlineData("ConstructedType", "MakeGenericType")]
    public void NonvirtualReflectionBaseCallsRetainTheirImplementation(string name, string operation)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var instructions = test.Method.ILBody!.Instructions;
        for (int i = 0; i < instructions.Count; i++) {
            if (instructions[i].Operand is MethodDesc { Name: var methodName } && methodName == operation) {
                var instruction = instructions[i];
                instruction.OpCode = ILCode.Call;
                instructions[i] = instruction;
            }
        }
        var expected = Assert.Throws<TargetInvocationException>(() => test.Emit().Invoke(null, null)).InnerException!.GetType();
        test.Optimize(defaultPipeline: true);
        Assert.Equal(expected, Assert.Throws<TargetInvocationException>(() => test.Emit().Invoke(null, null)).InnerException!.GetType());
    }

    private static bool IsGenericReflection(object? operand)
        => IsReflectionAccess(operand) || IsReflectionFactory(operand) || operand is MethodDesc method &&
            method.Name is "MakeGenericMethod" or "MakeGenericType" && method.DeclaringType.Namespace is "System" or "System.Reflection";

    private static void AssertNoGenericReflection(MethodDef method)
        => Assert.DoesNotContain(method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
}
