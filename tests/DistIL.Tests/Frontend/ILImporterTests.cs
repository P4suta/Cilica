namespace DistIL.Tests.Frontend;

using DistIL;
using DistIL.AsmIO;
using DistIL.Frontend;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;

[Collection("ModuleResolver")]
public class ILImporterTests(ModuleResolverFixture fixture)
{
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
        DistIL.IR.MethodBody Promote(string name)
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
        generic.Definition.ILBody = DistIL.CodeGen.Cil.ILGenerator.GenerateCode(generic);
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
        var emitted = DistIL.CodeGen.Cil.ILGenerator.GenerateCode(body);
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
