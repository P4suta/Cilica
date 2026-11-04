namespace Cilica.Tests.AsmIO;

using System.Reflection;
using Cilica.AsmIO;

public class ModuleResolverTests
{
    [Fact]
    public void GeneratedAndLoadedModulesHaveIdempotentDisposal()
    {
        var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var generated = resolver.Create("Generated");
        var loaded = resolver.Load(typeof(object).Assembly.Location);

        Assert.Same(loaded, resolver.Load(typeof(object).Assembly.Location));

        resolver.Dispose();
        resolver.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resolver.Resolve("Generated"));
        Assert.Throws<ObjectDisposedException>(() => resolver.Create("Another"));
        Assert.Throws<ObjectDisposedException>(() => generated.Save(new MemoryStream(), null, "unused.dll"));
    }

    [Fact]
    public void ResolutionUsesConfiguredNameBindings()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Create("Identity", new Version(1, 2, 3, 4));

        Assert.Same(module, resolver.Resolve(new AssemblyName(module.AsmName.FullName)));

        var version = new AssemblyName(module.AsmName.FullName) { Version = new(2, 0, 0, 0) };
        var token = new AssemblyName(module.AsmName.FullName);
        token.SetPublicKeyToken([1, 2, 3, 4, 5, 6, 7, 8]);

        var identities = new List<AssemblyName> { version, token };

        if (!(AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)) {
            identities.Add(new AssemblyName(module.AsmName.FullName) { CultureName = "ja-JP" });
        }

        foreach (var identity in identities) {
            Assert.Same(module, resolver.Resolve(identity));
        }
    }

    [Fact]
    public void RuntimeTypesUseTheSelectedRuntimeBinding()
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var current = resolver.Resolve(typeof(object).Assembly.GetName());

        Assert.Same(current, resolver.Import(typeof(object)).Module);
    }

    [Fact]
    public void UnusedReferencesAndForwardersResolveLazily()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "Forwarders.dll");

        Utils.WriteMetadataModule(path, metadata => {
            var core = metadata.AddAssemblyReference(metadata.GetOrAddString(typeof(object).Assembly.GetName().Name!), new Version(10, 0), default, default, 0, default);
            var missing = metadata.AddAssemblyReference(metadata.GetOrAddString("OptionalDependency"), new Version(1, 0), default, default, 0, default);
            metadata.AddTypeReference(missing, metadata.GetOrAddString("Optional"), metadata.GetOrAddString("UnusedReference"));
            metadata.AddExportedType((TypeAttributes)0x00200000, metadata.GetOrAddString("System"), metadata.GetOrAddString("String"), core, 0);
            metadata.AddExportedType((TypeAttributes)0x00200000, metadata.GetOrAddString("Optional"), metadata.GetOrAddString("UnusedForwarder"), missing, 0);
        });

        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Load(path);
        var text = resolver.Import(typeof(string));

        Assert.Same(text, module.FindType("System", "String"));
        Assert.Throws<FileNotFoundException>(() => module.FindType("Optional", "UnusedForwarder"));
        Assert.Same(text, module.FindType("System", "String"));
        Assert.Null(module.FindType("Unrelated", "Absent"));
    }

    [Fact]
    public void ForwarderCyclesProduceADiagnostic()
    {
        using var directory = new TemporaryDirectory();
        Write("First", "Second");
        Write("Second", "First");
        using var resolver = new ModuleResolver();
        resolver.AddSearchPaths([directory.Path]);
        var module = resolver.Resolve("First");

        Assert.Throws<BadImageFormatException>(() => module.FindType("Test", "Cycle"));
        Assert.Throws<BadImageFormatException>(() => module.FindType("Test", "Cycle"));

        void Write(string name, string target)
        {
            Utils.WriteMetadataModule(Path.Combine(directory.Path, name + ".dll"), metadata => {
                var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(target), new Version(1, 0), default, default, 0, default);
                metadata.AddExportedType((TypeAttributes)0x00200000, metadata.GetOrAddString("Test"), metadata.GetOrAddString("Cycle"), reference, 0);
            });
        }
    }

    [Fact]
    public void SearchPathOrderControlsTheNameBinding()
    {
        using var directory = new TemporaryDirectory();
        string first = Path.Combine(directory.Path, "v1"), second = Path.Combine(directory.Path, "v2");
        Write(first, new(1, 0, 0, 0));
        Write(second, new(2, 0, 0, 0));
        using var resolver = new ModuleResolver();
        resolver.AddSearchPaths([first, second]);
        resolver.AddTrustedSearchPaths();
        var module = resolver.Resolve(new AssemblyName("Versioned, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null"));

        Assert.Equal(new Version(1, 0, 0, 0), module.AsmName.Version);
        Assert.Same(module, resolver.Load(Path.Combine(second, "Versioned.dll")));

        static void Write(string path, Version version)
        {
            using var writer = new ModuleResolver();
            writer.AddTrustedSearchPaths();
            writer.Create("Versioned", version).Save(Path.Combine(path, "Versioned.dll"), savePdb: false);
        }
    }
}
