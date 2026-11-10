using CommandLine;

using Cilica;

[Verb("opt", isDefault: true, HelpText = "Optimizes a module.")]
class OptimizerOptions
{
    [Option("strict", HelpText = "Fail without saving output if any selected method cannot be compiled.")]
    public bool Strict { get; set; }

    [Option("verify-each", HelpText = "Verify IR after every optimization pass.")]
    public bool VerifyEachPass { get; set; }

    [Option("skip-module-initializers", HelpText = "Omit module initialization checks when modules are initialized before use, as with NativeAOT.")]
    public bool SkipModuleInitializers { get; set; }

    [Option("trimmer-compat", HelpText = "Prevent inlining methods with trimming dependency annotations.")]
    public bool PreserveTrimmingDependencies { get; set; }

    [Option("nativeaot-compat", HelpText = "Restrict transformations to IL constructs supported by NativeAOT.")]
    public bool NativeAotCompatibility { get; set; }

    [Option("public-sign", HelpText = "Keep the assembly public key and identity, with a zero-filled strong-name signature for .NET Core/.NET 5+. Does not preserve the publisher's cryptographic signature.")]
    public bool PublicSign { get; set; }

    [Option("strip-signatures", HelpText = "Remove Authenticode and strong-name signing, including the assembly public key. Changes assembly identity. Cannot be combined with --public-sign.")]
    public bool StripSignatures { get; set; }

    [Option("strip-readytorun", HelpText = "Discard ReadyToRun native code and emit retained IL for JIT compilation.")]
    public bool StripReadyToRun { get; set; }

    [Option('i', Required = true, HelpText = "Input module file path.")]
    public string InputPath { get; set; } = null!;

    [Option('o', HelpText = "Output module file path. If unspecified, the input module will be overwritten.")]
    public string? OutputPath { get; set; } = null;

    [Option('r', HelpText = "Module resolver search paths.")]
    public IEnumerable<string> ResolverPaths { get; set; } = null!;

    [Option("no-pdb-gen", HelpText = "Don't generate debug symbols.")]
    public bool DisablePdbGeneration { get; set; } = false;

    [Option("no-resolver-fallback", HelpText = "Don't use fallback search paths for module resolution.")]
    public bool NoResolverFallback { get; set; } = false;

    [Option("filter-unmarked", HelpText = "Only transform methods and classes marked with `OptimizeAttribute`.")]
    public bool FilterUnmarked { get; set; } = false;

    [Option("pass-dump-path", HelpText = "Output path for pass IR dump bundle (tools/PassDiffInspector.html). dump-filter")]
    public string? PassDumpBundlePath { get; set; } = null;

    [Option("dump-dir", HelpText = "Output directory for plain IR dumps.")]
    public string? DumpDir { get; set; } = null;

    [Option("dump-fmts", HelpText = "Comma-separated list of IR dump formats.\n")]
    public DumpFormats DumpFmts { get; set; } = DumpFormats.Graphviz;

    [Option("purge-dumps", HelpText = "Delete all files in `dump-dir`.")]
    public bool PurgeDumps { get; set; }

    [Option("dump-filter", HelpText = "Filters methods to be dumped using a wildcard pattern.")]
    public string? DumpMethodFilter { get; set; }

    [Option("pass-filter", HelpText = "Filters methods to be transformed using a wildcard pattern.")]
    public string? PassMethodFilter { get; set; }

    [Option("bisect", HelpText = "Colon-separated list of probabilities used to randomly disable optimizations from methods.")]
    public string? BisectFilter { get; set; }

    [Option("verbosity", HelpText = "Specifies logging verbosity.\n")]
    public LogLevel Verbosity { get; set; } = LogLevel.Info;

    [Option("log-stats", HelpText = "Log optimization pass statistics.")]
    public bool LogStats { get; set; }
}
