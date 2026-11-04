namespace Cilica.AsmIO;

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary> Target image properties retained when a managed assembly is rewritten. </summary>
public sealed record ModuleImage(PEHeaderBuilder Header, CorFlags Flags = CorFlags.ILOnly)
{
    internal byte[] Resources { get; init; } = [];

    internal int ResourceRva { get; init; }

    internal bool HasNativePayload { get; init; }

    internal bool HasAuthenticodeSignature { get; init; }

    internal static ModuleImage Read(PEReader reader)
    {
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader ?? throw new BadImageFormatException("Missing PE header");
        var cor = headers.CorHeader ?? throw new BadImageFormatException("Missing CLR header");
        var header = new PEHeaderBuilder(headers.CoffHeader.Machine, pe.SectionAlignment, pe.FileAlignment, pe.ImageBase,
            pe.MajorLinkerVersion, pe.MinorLinkerVersion, pe.MajorOperatingSystemVersion, pe.MinorOperatingSystemVersion,
            pe.MajorImageVersion, pe.MinorImageVersion, pe.MajorSubsystemVersion, pe.MinorSubsystemVersion,
            pe.Subsystem, pe.DllCharacteristics, headers.CoffHeader.Characteristics,
            pe.SizeOfStackReserve, pe.SizeOfStackCommit, pe.SizeOfHeapReserve, pe.SizeOfHeapCommit);
        var resources = pe.ResourceTableDirectory;

        return new(header, cor.Flags) {
            Resources = resources.Size == 0 ? [] : reader.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).ToArray(),
            ResourceRva = resources.RelativeVirtualAddress,
            HasNativePayload = cor.ManagedNativeHeaderDirectory.Size != 0 || cor.VtableFixupsDirectory.Size != 0 || pe.ExportTableDirectory.Size != 0,
            HasAuthenticodeSignature = pe.CertificateTableDirectory.Size != 0
        };
    }

    internal void ValidateForWriting(AssemblyName name, bool publicSign = false)
    {
        if (!Flags.HasFlag(CorFlags.ILOnly) || Flags.HasFlag(CorFlags.NativeEntryPoint) || HasNativePayload) {
            throw new NotSupportedException("Writing mixed-mode, native-entry-point, or precompiled native images is not supported");
        }

        if (HasAuthenticodeSignature) {
            throw new NotSupportedException("Rewriting Authenticode-signed images requires a signing provider");
        }
        if (publicSign) {
            if (name.GetPublicKey() is not { Length: > 0 }) {
                throw new NotSupportedException("Public signing requires an assembly public key");
            }
            GetSignatureSize(name);
        } else if (Flags.HasFlag(CorFlags.StrongNameSigned) || name.GetPublicKey() is { Length: > 0 }) {
            throw new NotSupportedException("Rewriting strong-name signed images requires a signing provider or explicit public signing (--public-sign)");
        }
    }

    internal static int GetSignatureSize(AssemblyName name)
    {
        // A strong-name key has a 12-byte header followed by a PUBLICKEYBLOB
        // (20-byte header and RSA modulus). The special ECMA key is 16 bytes.
        ReadOnlySpan<byte> key = name.GetPublicKey();
        if (key.SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 })) return 128;
        if (key.Length <= 32 || key[12] != 6 || key[13] != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(key[8..]) != key.Length - 12 ||
            BinaryPrimitives.ReadUInt32LittleEndian(key[20..]) != 0x31415352 ||
            BinaryPrimitives.ReadUInt32LittleEndian(key[24..]) != (uint)(key.Length - 32) * 8) {
            throw new BadImageFormatException("Invalid strong-name public key");
        }
        return key.Length - 32;
    }

    internal ResourceSectionBuilder? CreateResources() => Resources.Length == 0 ? null : new NativeResources(Resources, ResourceRva);

    sealed class NativeResources(byte[] data, int originalRva) : ResourceSectionBuilder
    {
        protected override void Serialize(BlobBuilder builder, SectionLocation location)
        {
            var copy = (byte[])data.Clone();
            var pending = new Stack<int>();
            var directories = new HashSet<int>();
            var entries = new HashSet<int>();
            pending.Push(0);

            while (pending.TryPop(out int directory)) {
                if (!directories.Add(directory)) {
                    throw new BadImageFormatException("Cyclic native resource directory");
                }

                var header = Slice(directory, 16);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);

                for (int i = 0; i < count; i++) {
                    uint target = BinaryPrimitives.ReadUInt32LittleEndian(Slice(checked(directory + 16 + i * 8 + 4), 4));
                    int offset = (int)(target & 0x7fffffff);

                    if ((target & 0x80000000) != 0) {
                        pending.Push(offset);
                        continue;
                    }

                    if (!entries.Add(offset)) {
                        continue;
                    }

                    var entry = Slice(offset, 16);
                    int relative = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry) - originalRva);
                    int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
                    Slice(relative, size);
                    BinaryPrimitives.WriteUInt32LittleEndian(entry, checked((uint)(location.RelativeVirtualAddress + relative)));
                }
            }

            builder.WriteBytes(copy);

            Span<byte> Slice(int offset, int size)
            {
                if (offset < 0 || size < 0 || offset > copy.Length - size) {
                    throw new BadImageFormatException("Native resource offset is outside its directory");
                }

                return copy.AsSpan(offset, size);
            }
        }
    }
}
