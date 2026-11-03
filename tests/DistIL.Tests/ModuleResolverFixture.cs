namespace DistIL.Tests;

using DistIL.AsmIO;

[CollectionDefinition("ModuleResolver")]
public class ModuleResolverFixture : ICollectionFixture<ModuleResolverFixture>, IDisposable
{
    public ModuleResolver Resolver { get; }

    public ModuleResolverFixture()
    {
        Resolver = new();
        Resolver.AddTrustedSearchPaths();
    }

    public void Dispose() => Resolver.Dispose();
}
