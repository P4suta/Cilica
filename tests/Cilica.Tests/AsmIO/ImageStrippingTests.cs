namespace Cilica.Tests.AsmIO;

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;

[Collection("ModuleResolver")]
public class ImageStrippingTests(ModuleResolverFixture fixture)
{
    const ModuleWriteOptions StripBoth = ModuleWriteOptions.StripSignatures | ModuleWriteOptions.StripReadyToRun;
    static readonly byte[] CertificatePayload = Encoding.ASCII.GetBytes("Cilica test certificate payload");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StrippingProducesUnsignedExecutableIL(bool strongName, bool readyToRun)
    {
        using var directory = new TemporaryDirectory();
        string input = CreateImage(directory.Path, strongName, readyToRun, authenticode: true);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(input);
        string originalIdentity = module.AsmName.FullName;
        var originalImage = module.Image!;
        Assert.True(originalImage.HasAuthenticodeSignature);
        Assert.Equal(readyToRun, originalImage.ReadyToRunFlags.HasValue);
        string output = Path.Combine(directory.Path, "output.dll");

        Assert.Throws<NotSupportedException>(() => module.Save(output, false));
        Assert.Throws<NotSupportedException>(() => module.Save(output, false, ModuleWriteOptions.StripReadyToRun));
        if (readyToRun) {
            Assert.Throws<NotSupportedException>(() => module.Save(output, false, ModuleWriteOptions.StripSignatures));
        }
        Assert.False(File.Exists(output));
        module.Save(output, false, StripBoth);
        var bytes = File.ReadAllBytes(output);
        using var pe = new PEReader(new MemoryStream(bytes));
        var cor = pe.PEHeaders.CorHeader!;
        Assert.True(cor.Flags.HasFlag(CorFlags.ILOnly));
        Assert.Equal((CorFlags)0, cor.Flags & (CorFlags.ILLibrary | CorFlags.StrongNameSigned));
        Assert.Equal(0, cor.StrongNameSignatureDirectory.Size);
        Assert.Equal(0, cor.StrongNameSignatureDirectory.RelativeVirtualAddress);
        Assert.Equal(0, cor.ManagedNativeHeaderDirectory.Size);
        Assert.Equal(0, cor.ManagedNativeHeaderDirectory.RelativeVirtualAddress);
        Assert.Equal(0, pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(CertificatePayload));
        var metadata = pe.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        Assert.True(definition.PublicKey.IsNil);
        Assert.False(definition.Flags.HasFlag(AssemblyFlags.PublicKey));
        // External assembly identities must not lose their signing keys.
        Assert.All(metadata.AssemblyReferences, reference =>
            Assert.NotEmpty(metadata.GetBlobBytes(metadata.GetAssemblyReference(reference).PublicKeyOrToken)));
        Assert.Empty(AssemblyName.GetAssemblyName(output).GetPublicKeyToken()!);
        AssertExecutable(bytes);

        Assert.Equal(originalIdentity, module.AsmName.FullName);
        Assert.Same(originalImage, module.Image);
        Assert.Throws<NotSupportedException>(() => module.Save(new MemoryStream(), null, "unchanged.dll"));
        using var repeated = new MemoryStream();
        module.Save(repeated, null, output, StripBoth);
        Assert.Equal(bytes, repeated.ToArray());

        using var outputResolver = new ModuleResolver();
        outputResolver.AddTrustedSearchPaths();
        var rewritten = outputResolver.Load(output);
        Assert.False(rewritten.Image!.HasAuthenticodeSignature);
        Assert.Null(rewritten.Image.ReadyToRunFlags);
        using var ordinarySave = new MemoryStream();
        rewritten.Save(ordinarySave, null, "unsigned.dll");
        AssertExecutable(ordinarySave.ToArray());
    }

    [Fact]
    public void ReadyToRunRemovalCanRetainThePublicSigningIdentity()
    {
        using var directory = new TemporaryDirectory();
        string input = CreateImage(directory.Path, strongName: true, readyToRun: true, authenticode: false);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(input);
        using var output = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => module.Save(output, null, "output.dll", publicSign: true));
        Assert.Equal(0, output.Length);
        module.Save(output, null, "output.dll", ModuleWriteOptions.StripReadyToRun | ModuleWriteOptions.PublicSign);
        using var pe = new PEReader(new MemoryStream(output.ToArray()));
        var cor = pe.PEHeaders.CorHeader!;
        Assert.True(cor.Flags.HasFlag(CorFlags.ILOnly | CorFlags.StrongNameSigned));
        Assert.False(cor.Flags.HasFlag(CorFlags.ILLibrary));
        Assert.Equal(0, cor.ManagedNativeHeaderDirectory.Size);
        Assert.Equal(module.AsmName.GetPublicKey()!.Length - 32, cor.StrongNameSignatureDirectory.Size);
        Assert.Equal(module.AsmName.FullName, pe.GetMetadataReader().GetAssemblyDefinition().GetAssemblyName().FullName);
        AssertExecutable(output.ToArray());
    }

    [Fact]
    public void ReadyToRunRemovalPreservesRuntimeGeneratedUnsafeAccessors()
    {
        using var directory = new TemporaryDirectory();
        string input = CreateImage(directory.Path, strongName: false, readyToRun: true, authenticode: false, unsafeAccessor: true);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(input);
        using var output = new MemoryStream();
        module.Save(output, null, "output.dll", StripBoth);
        using var pe = new PEReader(new MemoryStream(output.ToArray()));
        var metadata = pe.GetMetadataReader();
        var accessor = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
            .Single(method => metadata.GetString(method.Name) == "Abs");
        Assert.Equal(0, accessor.RelativeVirtualAddress);
        Assert.Single(accessor.GetCustomAttributes());
        AssertExecutable(output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x4644)]
    [InlineData(0xADC4)]
    [InlineData(0x7B79)]
    [InlineData(0x1993)]
    [InlineData(0x1992)]
    public void ReadyToRunRemovalDecodesNativeOsMachineIdentifiers(int os)
    {
        using var directory = new TemporaryDirectory();
        string input = CreateImage(directory.Path, strongName: false, readyToRun: true, authenticode: false);
        var bytes = File.ReadAllBytes(input);
        int peHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3c));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peHeader + 4), (ushort)((int)Machine.Amd64 ^ os));
        File.WriteAllBytes(input, bytes);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(input);
        using var output = new MemoryStream();
        module.Save(output, null, "output.dll", ModuleWriteOptions.StripReadyToRun);
        using var pe = new PEReader(new MemoryStream(output.ToArray()));
        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        AssertExecutable(output.ToArray());
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("native-entry")]
    [InlineData("exports")]
    [InlineData("vtable")]
    [InlineData("unknown-header")]
    [InlineData("stripped-il")]
    [InlineData("missing-il")]
    public void RemovalOptionsRejectRequiredNativeCodeWithoutReplacingOutput(string kind)
    {
        using var directory = new TemporaryDirectory();
        string input = CreateImage(directory.Path, strongName: false, readyToRun: true, authenticode: false, missingIL: kind == "missing-il");
        var bytes = File.ReadAllBytes(input);
        using (var pe = new PEReader(new MemoryStream(bytes))) {
            int cor = pe.PEHeaders.CorHeaderStartOffset;
            int r2r = RvaToOffset(pe, pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.RelativeVirtualAddress);
            switch (kind) {
                case "mixed":
                    bytes.AsSpan(cor + 64, 8).Clear();
                    break;
                case "native-entry":
                    Write32(bytes, cor + 16, (uint)(CorFlags.ILLibrary | CorFlags.NativeEntryPoint));
                    Write32(bytes, cor + 20, 0x1234); // RVA, not a MethodDef token
                    break;
                case "exports":
                    Write32(bytes, DataDirectoriesOffset(bytes), (uint)pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress);
                    Write32(bytes, DataDirectoriesOffset(bytes) + 4, 16);
                    break;
                case "vtable":
                    Write32(bytes, cor + 48, (uint)pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress);
                    Write32(bytes, cor + 52, 8);
                    break;
                case "unknown-header": Write32(bytes, r2r, 0x12345678); break;
                case "stripped-il": Write32(bytes, r2r + 8, 0x00000200); break;
            }
        }
        File.WriteAllBytes(input, bytes);
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(input);
        string output = Path.Combine(directory.Path, "output.dll");
        string pdb = Path.ChangeExtension(output, ".pdb");
        File.WriteAllText(output, "original assembly");
        File.WriteAllText(pdb, "original symbols");

        Assert.Throws<NotSupportedException>(() => module.Save(output, true, StripBoth, output + ".bak"));
        Assert.Equal("original assembly", File.ReadAllText(output));
        Assert.Equal("original symbols", File.ReadAllText(pdb));
        Assert.False(File.Exists(output + ".bak"));
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public void ConflictingSigningOptionsLeaveStreamsUntouched()
    {
        var module = CreateModule();
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => module.Save(output, null, "output.dll", StripBoth | ModuleWriteOptions.PublicSign));
        Assert.Throws<ArgumentOutOfRangeException>(() => module.Save(output, null, "output.dll", (ModuleWriteOptions)0x100));
        Assert.Equal(0, output.Length);
    }

    ModuleDef CreateModule()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32);
        body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        return body.Definition.Module;
    }

    string CreateImage(string directory, bool strongName, bool readyToRun, bool authenticode, bool missingIL = false, bool unsafeAccessor = false)
    {
        var module = CreateModule();
        if (unsafeAccessor) {
            var run = module.MethodDefs().Single();
            var accessor = run.DeclaringType.CreateMethod("Abs", PrimType.Int32,
                [new ParamDef(fixture.Resolver.Import(typeof(Math)), "target"), new ParamDef(PrimType.Int32, "value")],
                MethodAttributes.Private | MethodAttributes.Static);
            var attribute = fixture.Resolver.Import(typeof(System.Runtime.CompilerServices.UnsafeAccessorAttribute));
            accessor.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(attribute.FindMethod(".ctor"),
                [(int)System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod]));
            run.Body = new Cilica.IR.MethodBody(run);
            var builder = new Cilica.IR.Utils.IRBuilder(run.Body.CreateBlock());
            builder.CreateReturn(builder.CreateCall(accessor, ConstNull.Create(), ConstInt.CreateI(-42)));
            run.ILBody = ILGenerator.GenerateCode(run.Body);
        }
        if (strongName) module.AsmName.SetPublicKey(typeof(object).Assembly.GetName().GetPublicKey());
        if (missingIL) module.MethodDefs().Single().ILBody = null;
        if (readyToRun) {
            module.Image = new(new PEHeaderBuilder(machine: Machine.Amd64));
            var header = new byte[16];
            Write32(header, 0, 0x00525452); // READYTORUN_SIGNATURE
            Write32(header, 4, 16); // major 16, minor 0
            module.CreateEmbeddedResource("r2r-header", header);
        }
        string path = Path.Combine(directory, "input.dll");
        module.Save(path, false, publicSign: strongName);
        var bytes = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(bytes))) {
            if (readyToRun) {
                int cor = pe.PEHeaders.CorHeaderStartOffset;
                uint flags = (uint)((pe.PEHeaders.CorHeader!.Flags & ~CorFlags.ILOnly) | CorFlags.ILLibrary);
                Write32(bytes, cor + 16, flags);
                Write32(bytes, cor + 64, (uint)pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress + 4);
                Write32(bytes, cor + 68, 16);
            }
        }
        if (authenticode) {
            int offset = (bytes.Length + 7) & ~7;
            int size = (8 + CertificatePayload.Length + 7) & ~7;
            Array.Resize(ref bytes, offset + size);
            Write32(bytes, DataDirectoriesOffset(bytes) + 4 * 8, (uint)offset);
            Write32(bytes, DataDirectoriesOffset(bytes) + 4 * 8 + 4, (uint)size);
            Write32(bytes, offset, (uint)size);
            Write32(bytes, offset + 4, 0x00020200); // WIN_CERTIFICATE revision 2, PKCS#7 type
            CertificatePayload.CopyTo(bytes, offset + 8);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static int DataDirectoriesOffset(byte[] bytes)
    {
        int optionalHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3c)) + 24;
        return optionalHeader + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optionalHeader)) == 0x20b ? 112 : 96);
    }

    static int RvaToOffset(PEReader pe, int rva)
    {
        var section = pe.PEHeaders.SectionHeaders.Single(section => rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData);
        return section.PointerToRawData + rva - section.VirtualAddress;
    }

    static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    static void AssertExecutable(byte[] bytes)
    {
        var assembly = Assembly.Load(bytes);
        Assert.Equal(42, assembly.GetType("TestMethods")!.GetMethod("Run")!.Invoke(null, null));
    }
}
