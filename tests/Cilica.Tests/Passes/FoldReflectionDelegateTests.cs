namespace Cilica.Tests.Passes;

using Cilica.AsmIO;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("OpenInstance", 42)]
    [InlineData("OpenVirtual", 42)]
    [InlineData("OpenUntyped", 42)]
    [InlineData("OpenStaticFactory", 42)]
    [InlineData("OpenWithNullTarget", 42)]
    [InlineData("OpenNullNonvirtual", 42)]
    [InlineData("OpenMultipleCalls", 85)]
    [InlineData("OpenObserved", true)]
    [InlineData("OpenReceiverEvaluation", 1242)]
    [InlineData("OpenVirtualNullKeepsArgumentEffects", "2:NullReferenceException")]
    [InlineData("OpenStruct", 42)]
    [InlineData("OpenVirtualStruct", "42")]
    public void OpenInstanceDelegateInvocationsUseTheirSuppliedReceiver(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline, nativeAotCompatibility: nativeAot, simplifyDelegates: true);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            if (nativeAot) {
                Assert.Contains(test.Method.ILBody.Instructions, instruction => IsReflectionFactory(instruction.Operand));
                Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => instruction.OpCode is ILCode.Ldftn or ILCode.Ldvirtftn);
            }
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("OpenMulticast", 4)]
    [InlineData("OpenMutableCache", 42)]
    public void OpenDelegateCallsKeepUnknownAndMulticastTargets(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
            Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("OpenInterfaceDelegate", 42)]
    [InlineData("OpenAbstractDelegate", 42)]
    [InlineData("OpenGenericStructDelegate", "String:Object:hello:41")]
    public void OpenGenericAndAbstractDelegateCallsPreserveDispatch(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(nativeAotCompatibility: nativeAot, simplifyDelegates: true);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    private static bool IsDelegateInvoke(object? operand)
        => operand is MethodDesc { Name: "Invoke" } method && method.DeclaringType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) == true;

    [Theory]
    [InlineData("GenericStatic", 42)]
    [InlineData("UntypedStatic", 42)]
    [InlineData("GenericClosedInstance", 42)]
    [InlineData("UntypedClosedInstance", 42)]
    [InlineData("StaticFactoryOpen", 42)]
    [InlineData("StaticFactoryOpenWithFlag", 42)]
    [InlineData("StaticFactoryClosed", 42)]
    [InlineData("StaticFactoryClosedWithFlag", 42)]
    [InlineData("OpenInstance", 42)]
    [InlineData("OpenVirtual", 42)]
    [InlineData("ClosedVirtual", 42)]
    [InlineData("DeclaredVirtualIdentity", true)]
    [InlineData("BoxedVirtual", "42")]
    [InlineData("ClosedStatic", 42)]
    [InlineData("ClosedStaticNull", 42)]
    [InlineData("ClosedStaticFactory", 42)]
    [InlineData("BoundMissingArgument", true)]
    [InlineData("OpenStaticExplicitNull", 42)]
    [InlineData("BoxedStruct", 42)]
    [InlineData("OpenStruct", 42)]
    [InlineData("RefParameter", 42)]
    [InlineData("RefReturn", 42)]
    [InlineData("LargeStructReturn", 42L)]
    [InlineData("DelegateIdentity", true)]
    [InlineData("ClosedGenericType", 42)]
    [InlineData("DelegateInitialization", 42)]
    [InlineData("DelegateException", "failure")]
    [InlineData("DelegateInsideFilter", 42)]
    [InlineData("StaticFunctionPointer", 42)]
    [InlineData("CopiedFunctionPointerHandle", 42)]
    [InlineData("FunctionPointerIdentity", true)]
    [InlineData("GenericTypeFunctionPointer", "hello")]
    [InlineData("UnmanagedFunctionPointer", 42)]
    [InlineData("FunctionPointerInitialization", 42)]
    [InlineData("FunctionPointerMetadataObserved", "Add:42")]
    [InlineData("NativeImportDelegateCreation", true)]
    [InlineData("NativeImportPointerCreation", true)]
    public void KnownDelegateAndPointerFactoriesPreserveBehavior(string name, object expected)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedVirtualDelegatesPreserveMethodIdentity(bool defaultPipeline)
    {
        using var test = new Case("ClosedVirtualIdentity", "ReflectionDelegateCases");
        var expected = test.Original.Invoke(null, null);
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("ClosedNullInstance")]
    [InlineData("InvalidDelegateType")]
    [InlineData("SignatureMismatch")]
    [InlineData("FailureReturnsNull")]
    [InlineData("CovariantSignature")]
    [InlineData("OpenWithNonNullTarget")]
    [InlineData("BoundValueArgument")]
    [InlineData("OpenDelegateType")]
    [InlineData("GenericMethodDefinition")]
    [InlineData("NameBasedFactory")]
    [InlineData("DefaultFunctionPointerHandle")]
    [InlineData("MutatedFunctionPointerHandle")]
    [InlineData("OverwrittenFunctionPointerHandle")]
    [InlineData("InstanceFunctionPointer")]
    public void UnsupportedDelegateAndPointerFactoriesRemainReflective(string name)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        object? expected = test.Original.Invoke(null, null);
        test.Optimize();
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void InliningExposesDelegateFactories()
    {
        using var test = new Case("ThroughHelper", "ReflectionDelegateCases");
        test.Optimize(defaultPipeline: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void RuntimeMethodNamesKeepDelegateLookup()
    {
        using var test = new Case("RuntimeNamedDelegate", "ReflectionDelegateCases");
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, ["Add"]));
    }

    [Theory]
    [InlineData("GenericStatic")]
    [InlineData("StaticFunctionPointer")]
    public void FactoryFoldingPreservesRequestedTrimmingDependencies(string name)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Contains(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetMethod" });
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DelegateTypeInitializersRetainTheirActivationTiming(bool defaultPipeline, bool beforeFieldInit)
    {
        using var test = new Case("DelegateTypeInitialization", "ReflectionDelegateCases");
        test.AddDelegateTypeInitializer(beforeFieldInit);
        object? expected = test.Emit().Invoke(null, null);
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    private static bool IsReflectionFactory(object? operand)
        => operand is MethodDesc method &&
            (method.Name is "CreateDelegate" or "get_MethodHandle" && method.DeclaringType.Namespace is "System" or "System.Reflection" ||
                method.Name == "GetFunctionPointer" && method.DeclaringType.IsCorelibType(typeof(RuntimeMethodHandle)));
}
