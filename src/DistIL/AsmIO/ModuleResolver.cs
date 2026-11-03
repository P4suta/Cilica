namespace DistIL.AsmIO;

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

public class ModuleResolver : IDisposable
{
    internal readonly Dictionary<string, ModuleDef> _cache = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<ResolvingUtils.MethodSelector, MethodDesc> FunctionCache = new();
    internal readonly Dictionary<string, TypeDefOrSpec> TypeCache = new();

    private string[] _searchPaths = [];
    internal readonly ICompilationLogger? _logger;
    readonly Dictionary<string, ModuleDef> _paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    readonly HashSet<string> _frameworkAssemblies = new(StringComparer.OrdinalIgnoreCase);
    bool _disposed;

    public AssemblyResolutionPolicy ResolutionPolicy { get; }

    /// <summary> A reference to the <c>System.Private.CoreLib</c> assembly. </summary>
    public ModuleDef CoreLib => _coreLib ??= Resolve("System.Private.CoreLib");
    private ModuleDef? _coreLib;

    public SystemTypes SysTypes => _sysTypes ??= new(CoreLib);
    private SystemTypes? _sysTypes;

    public ModuleResolver(ICompilationLogger? logger = null, AssemblyResolutionPolicy resolutionPolicy = AssemblyResolutionPolicy.RuntimeCompatible)
    {
        _logger = logger;
        ResolutionPolicy = resolutionPolicy;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void AddSearchPaths(IEnumerable<string> paths)
    {
        ThrowIfDisposed();
        _searchPaths = _searchPaths
            .Concat(paths)
            .Select(FixRuntimePackRefPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in _searchPaths) {
            if (File.Exists(Path.Combine(path, "System.Private.CoreLib.dll")) ||
                File.Exists(Path.Combine(path, "Microsoft.WindowsDesktop.App.deps.json")) ||
                File.Exists(Path.Combine(path, "Microsoft.AspNetCore.App.deps.json"))) {
                foreach (var file in Directory.EnumerateFiles(path, "*.dll")) {
                    _frameworkAssemblies.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        // Runtime implementation paths provide the canonical definitions behind reference-assembly type forwarders.
        static string FixRuntimePackRefPath(string path)
        {
            // e.g. "C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\7.0.3\ref\net7.0"
            //                              shared                     ****      ***********
            string normPath = Path.GetFullPath(path).Replace('\\', '/');
            string implPath = Regex.Replace(normPath, @"(.+?\/)packs(\/Microsoft\.NETCore\.App)\.Ref(\/.+?)\/.+", "$1shared$2$3");
            return implPath != normPath && Directory.Exists(implPath) ? implPath : path;
        }
    }

    public void AddTrustedSearchPaths()
    {
        // https://docs.microsoft.com/en-us/dotnet/core/dependency-loading/default-probing
        string searchPaths = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        AddSearchPaths(searchPaths.Split(Path.PathSeparator).Select(Path.GetDirectoryName)!);
    }

    public TypeDefOrSpec? Import(Type type, [DoesNotReturnIf(true)] bool throwIfNotFound = true)
    {
        ThrowIfDisposed();
        Ensure.That(type.IsTypeDefinition || type.IsConstructedGenericType);

        var identity = type.Assembly.GetName();
        var mod = IsFrameworkAssembly(identity) ? Resolve(identity.Name!, throwIfNotFound) : Resolve(identity, throwIfNotFound);
        TypeDefOrSpec? resolved = null;

        if (mod != null) {
            resolved = type.IsNested || type.IsGenericType
                ? ImportGenericOrNestedType(mod, type, throwIfNotFound)
                : mod.FindType(type.Namespace, type.Name, throwIfNotFound);
        }
        if (resolved == null && throwIfNotFound) {
            throw new InvalidOperationException("Could not import the specified type");
        }
        return resolved;
    }

    private TypeDefOrSpec? ImportGenericOrNestedType(ModuleDef scope, Type type, bool throwIfNotFound)
    {
        var resolved = type.IsNested
            ? (Import(type.DeclaringType!, throwIfNotFound) as TypeDef)?.FindNestedType(type.Name)
            : scope.FindType(type.Namespace, type.Name, throwIfNotFound);

        if (resolved != null && type.IsConstructedGenericType) {
            var genArgs = type.GenericTypeArguments;
            var finalArgs = ImmutableArray.CreateBuilder<TypeDesc>(genArgs.Length);

            foreach (var genArg in genArgs) {
                var resolvedArg = Import(genArg, throwIfNotFound);
                if (resolvedArg == null) {
                    return null;
                }
                finalArgs.Add(resolvedArg);
            }
            return resolved.GetSpec(finalArgs.MoveToImmutable());
        }
        return resolved;
    }

    public ModuleDef? Resolve(AssemblyName name, [DoesNotReturnIf(true)] bool throwIfNotFound = true)
    {
        ThrowIfDisposed();
        string simpleName = name.Name ?? throw new ArgumentException("Assembly name is required", nameof(name));

        if (simpleName == "mscorlib" && ResolutionPolicy == AssemblyResolutionPolicy.RuntimeCompatible) {
            return Resolve("netstandard", throwIfNotFound);
        }

        if (_cache.TryGetValue(simpleName, out var bound)) {
            if (Matches(name, bound.AsmName)) {
                return bound;
            }

            if (!throwIfNotFound) {
                return null;
            }

            throw new FileLoadException($"Assembly '{name}' conflicts with the binding '{bound.AsmName}'");
        }

        var module = ResolveImpl(name);

        if (module == null && throwIfNotFound) {
            throw new FileNotFoundException($"Could not resolve assembly '{name}'");
        }

        return module!;
    }

    public ModuleDef? Resolve(string name, [DoesNotReturnIf(true)] bool throwIfNotFound = true)
        => Resolve(new AssemblyName(name), throwIfNotFound);

    protected virtual ModuleDef? ResolveImpl(AssemblyName name)
    {
        string? compatiblePath = null;

        foreach (string basePath in _searchPaths) {
            string path = Path.Combine(basePath, name.Name + ".dll");

            if (!File.Exists(path)) {
                continue;
            }

            var identity = AssemblyName.GetAssemblyName(path);

            if (!Matches(name, identity)) {
                continue;
            }

            if (name.Version == null || identity.Version == name.Version) {
                return Load(path);
            }

            compatiblePath ??= path;
        }

        return compatiblePath == null ? null : Load(compatiblePath);
    }

    private bool Matches(AssemblyName requested, AssemblyName actual)
    {
        if (!string.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (requested.CultureName != null && !string.Equals(requested.CultureName, actual.CultureName ?? "", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (requested.GetPublicKeyToken() is { } token && !token.AsSpan().SequenceEqual(actual.GetPublicKeyToken() ?? [])) {
            return false;
        }

        return requested.Version == null || requested.Version == actual.Version ||
            ResolutionPolicy == AssemblyResolutionPolicy.RuntimeCompatible && IsFrameworkAssembly(actual) && actual.Version >= requested.Version;
    }

    private bool IsFrameworkAssembly(AssemblyName identity)
    {
        string name = identity.Name ?? "";

        if (!_frameworkAssemblies.Contains(name)) {
            return false;
        }

        return Convert.ToHexString(identity.GetPublicKeyToken() ?? []) is
            "7CEC85D7BEA7798E" or "B03F5F7F11D50A3A" or "CC7B13FFCD2DDD51" or "B77A5C561934E089";
    }

    public ModuleDef Load(string path)
    {
        ThrowIfDisposed();
        path = Path.GetFullPath(path);

        if (_paths.TryGetValue(path, out var cached)) {
            return cached;
        }

        var module = new ModuleDef(this);
        module._loader = new ModuleLoader(path, module);

        try {
            if (_cache.TryGetValue(module.AsmName.Name!, out var existing)) {
                if (existing.AsmName.FullName != module.AsmName.FullName || existing.ModuleVersionId == Guid.Empty ||
                    existing.ModuleVersionId != module.ModuleVersionId) {
                    throw new FileLoadException($"A different image is already bound for '{module.AsmName.Name}'");
                }

                module.ReleaseReaders();
                _paths.Add(path, existing);

                return existing;
            }

            AddToCache(module.AsmName.Name!, module);
            _logger?.Debug($"Loading module '{module.AsmName.Name}, v{module.AsmName.Version}'");
            module._loader.Load();
            _paths.Add(path, module);

            return module;
        } catch {
            if (_cache.GetValueOrDefault(module.AsmName.Name!) == module) {
                _cache.Remove(module.AsmName.Name!);
            }

            module.ReleaseReaders();
            throw;
        }
    }

    public ModuleDef Create(string asmName, Version? ver = null)
    {
        ThrowIfDisposed();
        var module = new ModuleDef(this) {
            AsmName = new AssemblyName() {
                Name = asmName,
                Version = ver ?? new Version(1, 0, 0, 0),
            },
            ModName = asmName + ".dll"
        };
        module.CreateType(null, "<Module>", TypeAttributes.NotPublic); // global type required by the writer
        AddToCache(asmName, module);
        return module;
    }

    private void AddToCache(string name, ModuleDef module)
    {
        _cache.Add(name, module);
    }
    
    public void Dispose()
    {
        if (_disposed) {
            return;
        }

        _disposed = true;

        foreach (var module in _cache.Values) {
            module.ReleaseReaders();
        }

        _cache.Clear();
        _paths.Clear();
        _frameworkAssemblies.Clear();
        TypeCache.Clear();
        FunctionCache.Clear();
        _coreLib = null;
        _sysTypes = null;
    }
}

public enum AssemblyResolutionPolicy
{

    Exact,

    /// <summary> Permits version unification for signed runtime framework assemblies and the legacy mscorlib facade. </summary>
    RuntimeCompatible

}
