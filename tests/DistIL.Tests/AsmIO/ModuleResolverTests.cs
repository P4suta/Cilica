namespace DistIL.Tests.AsmIO;

using System.Reflection;
using DistIL.AsmIO;

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
    public void BindingsValidateVersionCultureAndPublicKeyToken()
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
            Assert.Throws<FileLoadException>(() => resolver.Resolve(identity));
            Assert.Null(resolver.Resolve(identity, throwIfNotFound: false));
        }
    }

    [Theory]
    [InlineData(AssemblyResolutionPolicy.RuntimeCompatible)]
    [InlineData(AssemblyResolutionPolicy.Exact)]
    public void FrameworkVersionUnificationIsExplicit(AssemblyResolutionPolicy policy)
    {
        using var resolver = new ModuleResolver(resolutionPolicy: policy);
        resolver.AddTrustedSearchPaths();
        var current = resolver.Resolve(typeof(object).Assembly.GetName());
        var reference = new AssemblyName(current.AsmName.FullName) { Version = new(4, 0, 0, 0) };

        if (policy == AssemblyResolutionPolicy.RuntimeCompatible) {
            Assert.Same(current, resolver.Resolve(reference));
        } else {
            Assert.Throws<FileLoadException>(() => resolver.Resolve(reference));
        }
    }

    [Fact]
    public void ExactCandidatesAreSelectedAcrossSearchPaths()
    {
        using var directory = new TemporaryDirectory();
        string first = Path.Combine(directory.Path, "v1"), second = Path.Combine(directory.Path, "v2");
        Write(first, new(1, 0, 0, 0));
        Write(second, new(2, 0, 0, 0));
        using var resolver = new ModuleResolver();
        resolver.AddSearchPaths([first, second]);
        resolver.AddTrustedSearchPaths();
        var module = resolver.Resolve(new AssemblyName("Versioned, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null"));

        Assert.Equal(new Version(2, 0, 0, 0), module.AsmName.Version);
        Assert.Throws<FileLoadException>(() => resolver.Load(Path.Combine(first, "Versioned.dll")));

        static void Write(string path, Version version)
        {
            using var writer = new ModuleResolver();
            writer.AddTrustedSearchPaths();
            writer.Create("Versioned", version).Save(Path.Combine(path, "Versioned.dll"), savePdb: false);
        }
    }
}
