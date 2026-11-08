namespace Cilica.Tests.Frontend;

using Cilica;
using Cilica.AsmIO;
using Cilica.Frontend;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;

[Collection("ModuleResolver")]
public class ILImporterTests(ModuleResolverFixture fixture)
{
    [Fact]
    public void ArrayInitializationWithKnownFieldDataImportsAsAnIntrinsic()
    {
        var resolver = fixture.Resolver;
        var source = Utils.CreateTestMethod(resolver, PrimType.Int32.CreateArray());
        var compilation = new Compilation(source.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        int[] expected = [10, 20, 30];
        var data = compilation.CreateStaticRva(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.AsSpan()));
        var initializer = resolver.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers)).FindMethod("InitializeArray");
        var builder = new IRBuilder(source.CreateBlock());
        var array = builder.CreateNewArray(PrimType.Int32, ConstInt.CreateI(expected.Length));
        builder.CreateCall(initializer, array, builder.Emit(new CilIntrinsic.LoadHandle(resolver, data)));
        builder.CreateReturn(array);
        source.Definition.ILBody = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(source);

        var body = ILImporter.ParseCode(source.Definition);
        Assert.Same(data, Assert.Single(body.Instructions().OfType<CilIntrinsic.InitializeArray>()).Data);
        source.Definition.Body = body;
        CompilationPipeline.CreateDefault(compilation).Run([source.Definition]).ThrowIfFailed();
        Assert.Equal(expected, Utils.Compile<Func<int[]>>(source.Definition.Body!)());
    }

    [Theory]
    [InlineData("ObjectByteOffset")]
    [InlineData("ObjectByteOffsetAcrossBlocks")]
    public unsafe void NativeAddressesStoredInByrefLocalsPreserveObjectOffsets(string name)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        string path = Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll");
        var module = resolver.Load(path);
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod(name);
        var original = System.Reflection.Assembly.Load(File.ReadAllBytes(path))
            .GetType("ImporterCases")!.GetMethod(name)!.CreateDelegate<ObjectByteOffset>();
        Check(original);

        for (int round = 0; round < 3; round++) {
            var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
            CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
            Check(Utils.Compile<ObjectByteOffset>(method.Body!));
            method.Body = null;
        }

        static void Check(ObjectByteOffset run)
        {
            byte[] bytes = new byte[32];
            object owner = bytes;
            fixed (byte* start = bytes) {
                nuint origin = System.Runtime.CompilerServices.Unsafe.As<object, nuint>(ref owner);
                Assert.Equal((nuint)start - origin, run(owner, ref bytes[0]));
                Assert.Equal((nuint)(start + 31) - origin, run(owner, ref bytes[31]));
            }
        }
    }

    [Fact]
    public void ReinterpretedByrefsPreserveSignedOffsets()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod("ReinterpretedByrefOffset");
        var bytes = new byte[32];

        for (int round = 0; round < 3; round++) {
            var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
            CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
            var run = Utils.Compile<ByrefOffset>(method.Body!);
            Assert.Equal((nint)31, run(ref bytes[0], ref bytes[31]));
            Assert.Equal((nint)(-31), run(ref bytes[31], ref bytes[0]));
            Assert.Equal((nint)0, run(ref bytes[0], ref bytes[0]));
            method.Body = null;
        }
    }

    [Fact]
    public unsafe void ByrefsStoredInNativeLocalsSupportIntegerOperations()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod("NativeLocalFromByref");
        byte* bytes = stackalloc byte[2];

        for (int round = 0; round < 3; round++) {
            var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
            CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
            var run = Utils.Compile<NativeFromByref>(method.Body!);
            Assert.Equal((nint)bytes & 1, run(ref bytes[0]));
            Assert.Equal((nint)(bytes + 1) & 1, run(ref bytes[1]));
            method.Body = null;
        }
    }

    delegate nuint ObjectByteOffset(object origin, ref byte target);
    delegate nint ByrefOffset(ref byte origin, ref byte target);
    delegate nint NativeFromByref(ref byte address);

    [Fact]
    public void InitializedByrefLocalsRemainNullAfterPromotion()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod("InitializedByrefLocal");
        var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
        CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
        var run = Utils.Compile<GetByref>(method.Body!);
        Assert.True(System.Runtime.CompilerServices.Unsafe.IsNullRef(ref run()));
    }

    delegate ref byte GetByref();

    [Theory]
    [InlineData("LoopAtSharedTryEntry", 0, "2134")]
    [InlineData("LoopAtSharedTryEntry", 1, "2134")]
    [InlineData("LoopAtSharedTryEntry", 2, "234")]
    [InlineData("LeaveIntoNestedSiblingTry", 0, "123456")]
    [InlineData("LeaveIntoNestedSiblingTry", 1, "12")]
    [InlineData("LeaveIntoNestedSiblingTry", 3, "12345")]
    [InlineData("LeaveWithinTry", 0, "1234")]
    [InlineData("LeaveWithinTry", 2, "123")]
    [InlineData("LeaveWithoutExceptionRegions", 0, "1")]
    [InlineData("FinallyStartsWithTry", 0, "1234")]
    [InlineData("FinallyStartsWithTry", 1, "123")]
    [InlineData("FinallyStartsWithTry", 2, "123")]
    [InlineData("FinallyStartsWithLoop", 0, "1234")]
    [InlineData("FinallyStartsWithLoop", 1, "123")]
    [InlineData("FinallyStartsWithLoop", 2, "12")]
    [InlineData("CatchBoxedValue", 0, "123")]
    [InlineData("CatchRestartsTry", 0, "2313")]
    [InlineData("CatchRestartsTry", 2, "23")]
    [InlineData("StackCounterLoop", 0, "111")]
    public void ExceptionControlFlowSurvivesRepeatedOptimization(string name, int failAt, string expected)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll");
        for (int round = 0; round < 4; round++) {
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
            var run = assembly.GetType("ExceptionRegionCases")!.GetMethod(name)!.CreateDelegate<Action<Action<int>>>();
            var trace = new List<int>();
            var failure = new InvalidOperationException();
            var actual = Record.Exception(() => run(value => {
                trace.Add(value);
                if (value == failAt) throw failure;
            }));
            Assert.Equal(expected, string.Concat(trace));
            Assert.Same(failAt == 0 ? null : failure, actual);
            if (round == 3) break;

            using var resolver = new ModuleResolver();
            resolver.AddTrustedSearchPaths();
            var module = resolver.Load(path);
            var method = (MethodDef)module.FindType(null, "ExceptionRegionCases")!.FindMethod(name);
            var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
            CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
            path = Path.Combine(directory.Path, $"round{round}.dll");
            module.Save(path, savePdb: false);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPrologueSequencePointsSurviveRepeatedOptimization(bool hasSequencePoint)
    {
        using var directory = new TemporaryDirectory();
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var body = Utils.CreateTestMethod(resolver, PrimType.Int32, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(42), body.Args[0])));
        var method = body.Definition;
        method.ILBody = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(body);
        method.Body = null;
        var document = new DebugSourceDocument() { Name = "division.cs", Language = DebugSourceDocument.CSharp };
        method.Module.GetDebugSymbols(create: true)!.SetMethodSymbols(method, new() {
            Document = document,
            SequencePoints = hasSequencePoint ? [new() {
                Document = document, Offset = method.ILBody.Instructions.Single(i => i.OpCode == ILCode.Div).Offset,
                StartLine = 42, EndLine = 42, StartColumn = 1, EndColumn = 10
            }] : []
        });
        string path = Path.Combine(directory.Path, "round.dll");
        OptimizeAndRun(method);
        for (int round = 0; round < 2; round++) {
            using var reader = new ModuleResolver();
            reader.AddTrustedSearchPaths();
            var module = reader.Load(path);
            OptimizeAndRun((MethodDef)module.FindType(null, "TestMethods")!.FindMethod("Run"));
        }

        void OptimizeAndRun(MethodDef method)
        {
            var compilation = new Compilation(method.Module, new VoidLogger(), new() { VerifyEachPass = true });
            CompilationPipeline.CreateDefault(compilation).Run([method]).ThrowIfFailed();
            method.Module.Save(path, savePdb: true);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".pdb")));
            var run = assembly.GetType("TestMethods")!.GetMethod("Run")!.CreateDelegate<Func<int, int>>();
            Assert.Equal(21, run(2));
            var exception = Assert.Throws<DivideByZeroException>(() => run(0));
            if (hasSequencePoint) {
                var frame = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true).GetFrame(0)!;
                Assert.Equal(42, frame.GetFileLineNumber());
                Assert.EndsWith("division.cs", frame.GetFileName());
            }
        }
    }

    [Theory]
    [InlineData("LoadPointerField")]
    [InlineData("StorePointerField")]
    [InlineData("AddressPointerField")]
    public unsafe void FieldAccessThroughALoadedNativePointerUsesThePointee(string name)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod(name);
        var body = method.Body = ILImporter.ParseCode(method);
        IRVerifier.Verify(body);
        nint* pointee = stackalloc nint[2] { 0x1111, 0x2222 };
        nint* storage = stackalloc nint[2] { (nint)pointee, 0x3333 };
        if (name == "StorePointerField") {
            Utils.Compile<Action<nint, nint>>(body)((nint)storage, 0x4444);
            Assert.Equal((nint)0x4444, pointee[1]);
        } else {
            nint result = Utils.Compile<Func<nint, nint>>(body)((nint)storage);
            Assert.Equal(name == "LoadPointerField" ? (nint)0x2222 : (nint)(pointee + 1), result);
        }
        Assert.Equal((nint)pointee, storage[0]);
        Assert.Equal((nint)0x3333, storage[1]);
        Assert.Equal((nint)0x1111, pointee[0]);
    }

    [Fact]
    public unsafe void ReferenceLoadsThroughVoidPointersKeepAnObjectStackValue()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod("ReadObjectFromUntypedPointer");
        var body = method.Body = ILImporter.ParseCode(method);
        IRVerifier.Verify(body);
        var run = Utils.Compile<ReadObject>(body);
        object value = new();
        Assert.Same(value, run(System.Runtime.CompilerServices.Unsafe.AsPointer(ref value)));
    }

    unsafe delegate object ReadObject(void* pointer);

    [Fact]
    public void InitializedLocalsKeepTheirDefaultValueDuringPromotion()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var type = module.FindType(null, "ImporterCases")!;
        Cilica.IR.MethodBody Promote(string name)
        {
            var method = (MethodDef)type.FindMethod(name);
            var body = method.Body = ILImporter.ParseCode(method);
            var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
            PassRunner.Run(new SsaPromotion(), new MethodTransformContext(compilation, body));
            Assert.DoesNotContain(body.Instructions().SelectMany(i => i.Operands.ToArray()), value => value is Undef);
            return body;
        }
        Assert.Equal(42, Utils.Compile<Func<int, int>>(Promote("InitializedLocalBeforeStore"))(42));
        var conditional = Utils.Compile<Func<bool, int>>(Promote("InitializedLocalOnUnassignedPath"));
        Assert.Equal(0, conditional(false));
        Assert.Equal(17, conditional(true));
        var generic = Promote("InitializedGeneric");
        generic.Definition.ILBody = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(generic);
        using var output = new MemoryStream();
        module.Save(output, null, "defaults.dll");
        var reflected = System.Reflection.Assembly.Load(output.ToArray()).GetType("ImporterCases")!.GetMethod("InitializedGeneric")!;
        Assert.Equal(0, reflected.MakeGenericMethod(typeof(int)).Invoke(null, null));
        Assert.Null(reflected.MakeGenericMethod(typeof(string)).Invoke(null, null));
        Assert.Equal(default(ValueTuple<int, long>), reflected.MakeGenericMethod(typeof(ValueTuple<int, long>)).Invoke(null, null));
    }

    [Fact]
    public void StaticFieldAccessConsumesButDoesNotDereferenceItsReceiver()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod("StaticFieldWithInstanceOpcodes");
        var body = method.Body = ILImporter.ParseCode(method);
        IRVerifier.Verify(body);
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body)(41));
    }

    [Theory]
    [InlineData("UncheckedStringCall")]
    [InlineData("UncheckedInterfaceCall")]
    [InlineData("UncheckedStringStore")]
    [InlineData("UncheckedStringReturn")]
    public void UncheckedObjectReferencesRemainUnchecked(string name)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(Path.Combine(AppContext.BaseDirectory, "TestAsm.IL.dll"));
        var method = (MethodDef)module.FindType(null, "ImporterCases")!.FindMethod(name);
        var body = method.Body = ILImporter.ParseCode(method);
        IRVerifier.Verify(body);
        Assert.Contains(body.Instructions(), i => i is CilIntrinsic.RefCast);
        var emitted = Cilica.CodeGen.Cil.ILGenerator.GenerateCode(body);
        Assert.DoesNotContain(emitted.Instructions, i => i.OpCode == ILCode.Castclass);
        if (name == "UncheckedInterfaceCall") {
            Assert.Equal(3, Utils.Compile<Func<IComparable, int>>(body)("abc"));
        } else if (name == "UncheckedStringCall") {
            Assert.Equal(3, Utils.Compile<Func<object, int>>(body)("abc"));
            Assert.Throws<NullReferenceException>(() => Utils.Compile<Func<object, int>>(body)(null!));
        } else {
            var run = Utils.Compile<Func<object, string>>(body);
            Assert.Equal("abc", run("abc"));
            Assert.Null(run(null!));
        }
    }

    [Fact]
    public void PinnedLocalsSurviveImportAndSsaPromotion()
    {
        var module = fixture.Resolver.Resolve("TestAsm.IL");
        var method = module.FindType(null, "ImporterCases")!.FindMethod("PinnedVar1");
        var body = ILImporter.ParseCode(((MethodDefOrSpec)method).Definition);
        var pinned = Assert.Single(body.LocalVars().Where(slot => slot.IsPinned));

        Assert.Equal(PrimType.Int32.CreateByref(), pinned.Type);
        Assert.False(body.InitLocals);

        var writer = new StringWriter();
        IRPrinter.ExportPlain(body, writer);

        string expected = File.ReadAllText("Passes/Cases/Importer.ethil");
        var result = FileChecker.Check(expected, writer.ToString(), StringComparison.Ordinal);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Failures[0].Message + "\n" + writer);

        IRVerifier.Verify(body);
        var compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true });
        PassRunner.Run(new SsaPromotion(), new MethodTransformContext(compilation, body));

        Assert.Contains(pinned, body.LocalVars());

        body.Definition.Body = body;
        var run = Utils.Compile<UpdateValue>(body);
        int value = 21;
        run(ref value);

        Assert.Equal(21 ^ 0x5555, value);
    }

    delegate void UpdateValue(ref int value);
}
