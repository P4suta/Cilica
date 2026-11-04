using System.Text.RegularExpressions;

using Cilica;
using Cilica.AsmIO;
using Cilica.IR.Utils;
using Cilica.Passes;

class DumpPass : IMethodPass
{
    readonly Dictionary<MethodDef, string> _methodNames = new();
    readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<ModuleDef> _namedModules = new();

    public string BaseDir { get; init; } = null!;
    public Predicate<MethodDef>? Filter { get; init; }
    public DumpFormats Formats { get; init; }

    public MethodPassResult Run(MethodTransformContext ctx)
    {
        if (Filter == null || Filter.Invoke(ctx.Method.Definition)) {
            var def = ctx.Method.Definition;
            string name = GetOutputName(def);

            if (Formats.HasFlag(DumpFormats.Graphviz)) {
                IRPrinter.ExportDot(ctx.Method, $"{BaseDir}/{name}.dot");
            }
            if (Formats.HasFlag(DumpFormats.Plaintext)) {
                IRPrinter.ExportPlain(ctx.Method, $"{BaseDir}/{name}.txt");
            }
            if (Formats.HasFlag(DumpFormats.Forest)) {
                IRPrinter.ExportForest(ctx.Method, $"{BaseDir}/{name}_forest.txt");
            }
        }

        return MethodInvalidations.None;
    }

    private string GetOutputName(MethodDef method)
    {
        if (_namedModules.Add(method.Module)) {
            foreach (var definition in method.Module.MethodDefs()) {
                ReserveName(definition);
            }
        }

        return _methodNames.TryGetValue(method, out var name) ? name : ReserveName(method);
    }

    private string ReserveName(MethodDef method)
    {
        string stem = Regex.Replace($"{method.DeclaringType.Name}::{method.Name}", @"[\x00-\x1F:*?\/\\""<>|]", "_");
        string name = stem;
        int suffix = 2;

        while (!_usedNames.Add(name)) {
            name = stem + "_" + suffix++;
        }

        _methodNames.Add(method, name);

        return name;
    }
}
[Flags]
enum DumpFormats
{
    None = 0,
    Graphviz = 1 << 0,
    Plaintext = 1 << 1,
    Forest = 1 << 2
}
