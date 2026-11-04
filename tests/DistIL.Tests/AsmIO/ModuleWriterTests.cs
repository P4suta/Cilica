namespace DistIL.Tests.AsmIO;

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.Util;

[Collection("ModuleResolver")]
public class ModuleWriterTests(ModuleResolverFixture fixture)
{
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
