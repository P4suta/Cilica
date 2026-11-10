namespace Cilica.Tests.AsmIO;

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.Util;

[Collection("ModuleResolver")]
public class ModuleWriterTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FieldCustomModifiersRoundTrip(bool required)
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule();
        var owner = module.TypeDefs.Single(type => type.Name == "TestMethods");
        var modifier = module.Resolver.Import(typeof(System.Runtime.CompilerServices.IsVolatile));
        owner.CreateField("Value", new TypeSig(PrimType.Int32, [new TypeModifier(modifier, required)]),
            System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var path = Path.Combine(directory.Path, "fields.dll");
        module.Save(path, savePdb: false);

        for (int pass = 0; pass < 2; pass++) {
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
            var field = assembly.GetType("TestMethods")!.GetField("Value")!;
            Assert.Equal([typeof(System.Runtime.CompilerServices.IsVolatile)], required
                ? field.GetRequiredCustomModifiers() : field.GetOptionalCustomModifiers());
            field.SetValue(null, 42);
            Assert.Equal(42, field.GetValue(null));
            using var resolver = new ModuleResolver();
            resolver.AddTrustedSearchPaths();
            var rewritten = resolver.Load(path);
            var next = Path.Combine(directory.Path, "pass" + pass + ".dll");
            rewritten.Save(next, savePdb: false);
            path = next;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("result")]
    public void ParameterMetadataNamesRoundTrip(string? name)
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule();
        var owner = module.TypeDefs.Single(type => type.Name == "TestMethods");
        var method = owner.CreateMethod("Identity", PrimType.Int32, [new ParamDef(PrimType.Int32, name)],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
        method.ReturnParam.Name = name;
        var il = new ILAssembler();
        il.Emit(ILCode.Ldarg_0);
        il.Emit(ILCode.Ret);
        method.ILBody = il.Assemble(null);
        var path = Path.Combine(directory.Path, "parameters.dll");
        module.Save(path, savePdb: false);

        for (int pass = 0; pass < 2; pass++) {
            var loaded = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
            var reflected = loaded.GetType("TestMethods")!.GetMethod("Identity")!;
            Assert.Equal(name, reflected.ReturnParameter.Name);
            Assert.Equal(name, reflected.GetParameters()[0].Name);
            Assert.Equal(System.Reflection.ParameterAttributes.None, reflected.ReturnParameter.Attributes);
            Assert.Equal(42, reflected.Invoke(null, [42]));
            using var resolver = new ModuleResolver();
            resolver.AddTrustedSearchPaths();
            var rewritten = resolver.Load(path);
            var next = Path.Combine(directory.Path, "pass" + pass + ".dll");
            rewritten.Save(next, savePdb: false);
            path = next;
        }
    }

    [Fact]
    public void VarArgDefinitionsAndCallSiteSentinelsRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule();
        var owner = module.TypeDefs.Single(type => type.Name == "TestMethods");
        var target = owner.CreateMethod("VarArgTarget", PrimType.Int32, [new ParamDef(PrimType.Int32, "fixedArg")],
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
        target.CallConv = CallConvention.VarArgs;
        var il = new ILAssembler();
        il.Emit(ILCode.Ldarg_0);
        il.Emit(ILCode.Ret);
        target.ILBody = il.Assemble(null);
        var signature = new MethodSig(PrimType.Int32, [PrimType.Int32, PrimType.String],
            new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.VarArgs, 0), requiredParameterCount: 1);
        var reference = new MethodReference(target, target.Name, signature);
        var genericSite = new MethodReference(target, target.Name, new MethodSig(PrimType.Int32,
            [PrimType.Int32, GenericParamType.GetUnboundM(0)], signature.Header, requiredParameterCount: 1));
        var specializedSite = Assert.IsType<MethodReference>(genericSite.GetSpec(new GenericContext(methodArgs: [PrimType.String])));
        Assert.Equal(PrimType.String, specializedSite.Signature.ParamTypes[1].Type);
        var caller = owner.FindMethod("Run") as MethodDef;
        il = new ILAssembler();
        il.EmitLdcI4(42);
        il.Emit(ILCode.Ldstr, "optional");
        il.Emit(ILCode.Call, reference);
        il.Emit(ILCode.Ret);
        caller!.ILBody = il.Assemble(null);
        caller.Body = null;
        string path = Path.Combine(directory.Path, "varargs.dll");
        module.Save(path, false);

        for (int pass = 0; pass < 2; pass++) {
            using var resolver = new ModuleResolver();
            resolver.AddTrustedSearchPaths();
            var loaded = resolver.Load(path);
            var loadedTarget = loaded.MethodDefs().Single(method => method.Name == target.Name);
            Assert.Equal(CallConvention.VarArgs, loadedTarget.CallConv);
            var call = loaded.MethodDefs().Single(method => method.Name == "Run").ILBody!.Instructions.Single(inst => inst.OpCode == ILCode.Call);
            var site = Assert.IsType<MethodReference>(call.Operand);
            Assert.Same(loadedTarget, site.Parent);
            Assert.Equal(1, site.Signature.RequiredParameterCount);
            Assert.Equal(2, site.ParamSig.Count);
            string next = Path.Combine(directory.Path, "pass" + pass + ".dll");
            loaded.Save(next, false);
            path = next;
        }
    }

    ModuleDef CreateModule(bool symbols = false)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        var module = body.Definition.Module;

        if (symbols) {
            var document = new DebugSourceDocument() { Name = "source.cs", Language = DebugSourceDocument.CSharp };
            module.GetDebugSymbols(create: true)!.SetMethodSymbols(body.Definition, new() {
                Document = document,
                SequencePoints = [new() { Document = document, Offset = 0, StartLine = 42, EndLine = 42, StartColumn = 1, EndColumn = 8 }]
            });
        }

        return module;
    }

    [Theory]
    [InlineData(Machine.I386, CorFlags.ILOnly | CorFlags.Requires32Bit)]
    [InlineData(Machine.I386, CorFlags.ILOnly | CorFlags.Prefers32Bit)]
    [InlineData(Machine.Amd64, CorFlags.ILOnly)]
    [InlineData(Machine.Arm64, CorFlags.ILOnly)]
    public void RoundTripsPreserveTargetImageProperties(Machine machine, CorFlags flags)
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule();
        var header = new PEHeaderBuilder(machine: machine, subsystem: Subsystem.WindowsGui,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll,
            sizeOfStackReserve: 2 * 1024 * 1024, majorSubsystemVersion: 6);
        module.Image = new(header, flags);
        string path = Path.Combine(directory.Path, "input.dll");
        module.Save(path, false);
        using var reader = new ModuleResolver();
        reader.AddTrustedSearchPaths();
        var loaded = reader.Load(path);
        using var stream = new MemoryStream();
        loaded.Save(stream, null, "output.dll");
        stream.Position = 0;
        using var pe = new PEReader(stream);

        Assert.Equal(machine, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(flags, pe.PEHeaders.CorHeader!.Flags);
        Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
        Assert.Equal(header.SizeOfStackReserve, pe.PEHeaders.PEHeader.SizeOfStackReserve);
        Assert.Equal(header.MajorSubsystemVersion, pe.PEHeaders.PEHeader.MajorSubsystemVersion);
    }

    [Fact]
    public void RewritingPreservesRootAndNestedTypeForwarders()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "Forwarders.dll");

        Utils.WriteMetadataModule(path, metadata => {
            var core = metadata.AddAssemblyReference(metadata.GetOrAddString(typeof(object).Assembly.GetName().Name!), new Version(10, 0), default, default, 0, default);
            var parent = metadata.AddExportedType((System.Reflection.TypeAttributes)0x00200000,
                metadata.GetOrAddString("System"), metadata.GetOrAddString("Environment"), core, 0);
            metadata.AddExportedType(System.Reflection.TypeAttributes.NestedPublic, default, metadata.GetOrAddString("SpecialFolder"), parent, 0);
        });

        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(path);
        string output = Path.Combine(directory.Path, "output.dll");
        module.Save(output, savePdb: false);

        using var reader = new ModuleResolver();
        reader.AddTrustedSearchPaths();
        var rewritten = reader.Load(output);

        Assert.Equal(2, rewritten.ExportedTypes.Count);
        Assert.Same(reader.Import(typeof(Environment)), rewritten.FindType("System", "Environment"));
        Assert.Contains(reader.Import(typeof(Environment.SpecialFolder)), rewritten.ExportedTypes);
    }

    [Fact]
    public void RewritingPreservesForwardersToUnavailableAssemblies()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "Forwarders.dll");
        byte[] token = [1, 2, 3, 4, 5, 6, 7, 8];
        Utils.WriteMetadataModule(path, metadata => {
            var missing = metadata.AddAssemblyReference(metadata.GetOrAddString("OptionalDependency"), new Version(1, 2, 3, 4),
                default, metadata.GetOrAddBlob(token), 0, default);
            var parent = metadata.AddExportedType((System.Reflection.TypeAttributes)0x00200000,
                metadata.GetOrAddString("Optional"), metadata.GetOrAddString("Parent"), missing, 0);
            metadata.AddExportedType(System.Reflection.TypeAttributes.NestedPublic, default, metadata.GetOrAddString("Child"), parent, 0);
        });
        using var resolver = new ModuleResolver();
        var module = resolver.Load(path);
        using var output = new MemoryStream();
        module.Save(output, null, "output.dll");
        output.Position = 0;
        using var pe = new PEReader(output);
        var reader = pe.GetMetadataReader();
        var types = reader.ExportedTypes.Select(reader.GetExportedType).ToArray();

        Assert.Equal(2, types.Length);
        Assert.Equal("Parent", reader.GetString(types[0].Name));
        Assert.Equal("Optional", reader.GetString(types[0].Namespace));
        Assert.True(types[0].IsForwarder);
        var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)types[0].Implementation);
        Assert.Equal("OptionalDependency", reader.GetString(assembly.Name));
        Assert.Equal(new Version(1, 2, 3, 4), assembly.Version);
        Assert.Equal(token, reader.GetBlobBytes(assembly.PublicKeyOrToken));
        Assert.Equal("Child", reader.GetString(types[1].Name));
        Assert.Equal(reader.ExportedTypes.First(), (ExportedTypeHandle)types[1].Implementation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrippedImagesWithoutPdbOutputHaveAValidDebugDirectory(bool symbols)
    {
        var module = CreateModule(symbols);
        var machine = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch {
            System.Runtime.InteropServices.Architecture.X64 => Machine.Amd64,
            System.Runtime.InteropServices.Architecture.Arm64 => Machine.Arm64,
            _ => Machine.I386
        };
        module.Image = new(new PEHeaderBuilder(machine: machine, sectionAlignment: 4096, fileAlignment: 4096,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll | Characteristics.LargeAddressAware));
        using var output = new MemoryStream();
        module.Save(output, null, "rewritten.dll", ModuleWriteOptions.StripSignatures);

        using var pe = new PEReader(new MemoryStream(output.ToArray()));
        var debug = pe.PEHeaders.PEHeader!.DebugTableDirectory;
        Assert.True(debug.Size > 0 || debug.RelativeVirtualAddress == 0);
        var assembly = System.Reflection.Assembly.Load(output.ToArray());
        var run = assembly.GetType("TestMethods")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(42, run());
    }

    [Fact]
    public void PeAndPortablePdbIdentifiersAreContentBased()
    {
        var module = CreateModule(symbols: true);
        using var first = new MemoryStream();
        using var pdb1 = new MemoryStream();
        using var second = new MemoryStream();
        using var pdb2 = new MemoryStream();
        module.Save(first, pdb1, "output.dll");
        module.Save(second, pdb2, "output.dll");

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(pdb1.ToArray(), pdb2.ToArray());

        Guid initial = Mvid(first.ToArray());

        Assert.NotEqual(Guid.Empty, initial);

        var method = module.MethodDefs().Single();
        method.Body!.EntryBlock.SetBranch(new ReturnInst(ConstInt.CreateI(7)));
        method.ILBody = ILGenerator.GenerateCode(method.Body);
        using var changed = new MemoryStream();
        module.Save(changed, null, "output.dll");

        Assert.NotEqual(initial, Mvid(changed.ToArray()));

        static Guid Mvid(byte[] data)
        {
            using var pe = new PEReader(new MemoryStream(data));
            var metadata = pe.GetMetadataReader();

            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
    }

    [Fact]
    public void RewritingRelocatesNativeVersionResources()
    {
        using var directory = new TemporaryDirectory();
        using var reader = new ModuleResolver();
        reader.AddTrustedSearchPaths();
        string original = Path.Combine(AppContext.BaseDirectory, "TestAsm.dll");
        var module = reader.Load(original);

        Assert.NotEmpty(module.Image!.Resources);

        module.CreateEmbeddedResource("large", new byte[100_000]);
        string output = Path.Combine(directory.Path, "output.dll");
        module.Save(output, false);
        using var pe = new PEReader(File.OpenRead(output));

        Assert.NotEqual(module.Image.ResourceRva, pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress);

        var expected = FileVersionInfo.GetVersionInfo(original);
        var actual = FileVersionInfo.GetVersionInfo(output);

        Assert.Equal(expected.FileVersion, actual.FileVersion);
        Assert.Equal(expected.ProductName, actual.ProductName);
    }

    [Fact]
    public void RejectedImagesLeaveAssemblyAndPdbUntouched()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "output.dll"), pdb = Path.ChangeExtension(path, ".pdb");
        File.WriteAllText(path, "original assembly");
        File.WriteAllText(pdb, "original symbols");
        var module = CreateModule(symbols: true);
        module.Image = new(PEHeaderBuilder.CreateLibraryHeader(), CorFlags.ILOnly | CorFlags.StrongNameSigned);

        Assert.Throws<NotSupportedException>(() => module.Save(path, true));
        Assert.Equal("original assembly", File.ReadAllText(path));
        Assert.Equal("original symbols", File.ReadAllText(pdb));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public void PublicSigningPreservesIdentityAndReservesAnEmptySignature()
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule();
        module.AsmName.SetPublicKey(typeof(object).Assembly.GetName().GetPublicKey());
        module.AsmName.CultureName = "";
        module.Image = new(PEHeaderBuilder.CreateLibraryHeader(), CorFlags.ILOnly | CorFlags.StrongNameSigned);
        string expectedIdentity = module.AsmName.FullName;
        string path = Path.Combine(directory.Path, "output.dll");

        Assert.Throws<NotSupportedException>(() => module.Save(path, false));
        Assert.False(File.Exists(path));

        module.Save(path, false, publicSign: true);
        Assert.Equal(expectedIdentity, System.Reflection.AssemblyName.GetAssemblyName(path).FullName);
        using var pe = new PEReader(File.OpenRead(path));
        var header = pe.PEHeaders.CorHeader!;
        Assert.True(header.Flags.HasFlag(CorFlags.StrongNameSigned));
        Assert.Equal(module.AsmName.GetPublicKey()!.Length - 32, header.StrongNameSignatureDirectory.Size);
        var signature = pe.GetSectionData(header.StrongNameSignatureDirectory.RelativeVirtualAddress)
            .GetContent(0, header.StrongNameSignatureDirectory.Size);
        Assert.All(signature, value => Assert.Equal((byte)0, value));

        var loaded = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
        Assert.Equal(42, loaded.GetType("TestMethods")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void PublicSigningRequiresAKeyAndDoesNotDiscardAuthenticode()
    {
        var module = CreateModule();
        using var stream = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => module.Save(stream, null, "output.dll", publicSign: true));
        Assert.Equal(0, stream.Length);

        module.AsmName.SetPublicKey(typeof(object).Assembly.GetName().GetPublicKey());
        module.Image = new(PEHeaderBuilder.CreateLibraryHeader()) { HasAuthenticodeSignature = true };
        Assert.Throws<NotSupportedException>(() => module.Save(stream, null, "output.dll", publicSign: true));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void PublicationRollsBackEarlierFilesWhenALaterDestinationFails()
    {
        using var directory = new TemporaryDirectory();
        string first = Path.Combine(directory.Path, "first"), blocked = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(first, "original");
        Directory.CreateDirectory(blocked);

        using (var output = new AtomicOutput()) {
            output.Create(first).Write([1, 2, 3]);
            output.Create(blocked).Write([4, 5, 6]);

            Assert.ThrowsAny<IOException>(output.Commit);
        }

        Assert.Equal("original", File.ReadAllText(first));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void PortablePdbReadersRemainOwnedUntilResolverDisposal()
    {
        using var directory = new TemporaryDirectory();
        var module = CreateModule(symbols: true);
        string path = Path.Combine(directory.Path, "output.dll");
        module.Save(path, true);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var loaded = resolver.Load(path);
        var symbols = loaded.GetDebugSymbols()!;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal(42, symbols.GetMethodSymbols(loaded.MethodDefs().Single())!.SequencePoints[0].StartLine);

        resolver.Dispose();

        Assert.Throws<ObjectDisposedException>(() => symbols.GetDocuments());
    }

    [Fact]
    public void SuccessfulReplacementPreservesRequestedBackups()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "output.dll"), pdb = Path.ChangeExtension(path, ".pdb");
        File.WriteAllText(path, "original assembly");
        File.WriteAllText(pdb, "original symbols");
        CreateModule(symbols: true).Save(path, true, path + ".bak");

        Assert.Equal("original assembly", File.ReadAllText(path + ".bak"));
        Assert.Equal("original symbols", File.ReadAllText(pdb + ".bak"));

        using var pe = new PEReader(File.OpenRead(path));

        Assert.True(pe.HasMetadata);
        Assert.Equal(4, Directory.GetFiles(directory.Path).Length);
    }
}
