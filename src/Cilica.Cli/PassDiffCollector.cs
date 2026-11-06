using System.Text.Json;
using System.Text.Json.Serialization;

using Cilica;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

class PassDiffCollector(string outputPath, Predicate<MethodDef>? filter, DumpFormats formats) : IPassInspector
{
    Dictionary<MethodDef, MethodTimeline> _entries = new();

    void IPassInspector.OnBeforePass(IMethodPass pass, MethodTransformContext ctx)
    {
        var method = ctx.Method.Definition;

        if (filter != null && !filter.Invoke(method)) return;

        if (!_entries.ContainsKey(method)) {
            var entry = new MethodTimeline() { Name = method.DeclaringType.Name + "::" + method.Name };
            entry.TakeSnapshot(method, "(initial)", captureCode: true);

            _entries.Add(method, entry);
        }
    }
    void IPassInspector.OnAfterPass(IMethodPass pass, MethodTransformContext ctx, MethodPassResult result)
    {
        var method = ctx.Method.Definition;

        if (_entries.TryGetValue(method, out var entry)) {
            bool changed = result.Changes != MethodInvalidations.None;

            // sanity hash check
            if (!changed) {
                int newHash = ComputeHash(method.Body!);
                changed = newHash != entry.LastHash;
                entry.LastHash = newHash;
            }
            entry.TakeSnapshot(method, pass.GetType().Name, captureCode: changed);
        }
    }

    void IPassInspector.OnFinish(Compilation comp)
    {
        using var fs = File.Create(outputPath);

        var opts = new JsonSerializerOptions() {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            Converters = { new MethodSnapshot.Converter(formats) }
        };
        JsonSerializer.Serialize(fs, _entries.Values, opts);
        
        comp.Logger.Info($"Collected pass dumps for {_entries.Count} methods.");
    }

    private static int ComputeHash(MethodBody body)
    {
        var hc = new HashCode();
        hc.Add(body.NumBlocks);

        foreach (var inst in body.Instructions()) {
            hc.Add(inst);

            foreach (var oper in inst.Operands) {
                hc.Add(oper);
            }
        }
        return hc.ToHashCode();
    }

    class MethodTimeline
    {
        public string Name { get; set; } = "";
        public List<MethodSnapshot> Passes { get; set; } = [];

        [JsonIgnore] public int LastHash { get; set; }

        public void TakeSnapshot(MethodDef method, string passName, bool captureCode)
        {
            var sn = new MethodSnapshot() { PassName = passName };

            if (captureCode) {
                var sw = new StringWriter();
                IRPrinter.ExportPlain(method.Body!, sw);
                sn.PlainCode = sw.ToString();

                sw.GetStringBuilder().Clear();

                IRPrinter.ExportDot(method.Body!, sw);
                sn.GraphvizCode = sw.ToString();
            }
            Passes.Add(sn);
        }
    }
    struct MethodSnapshot
    {
        public string PassName { get; set; }
        public string? PlainCode { get; set; }
        public string? GraphvizCode { get; set; }

        internal class Converter(DumpFormats formats) : JsonConverter<MethodSnapshot>
        {
            public override MethodSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                throw new NotImplementedException();
            }

            public override void Write(Utf8JsonWriter writer, MethodSnapshot value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WriteString("passName", value.PassName);
                if (formats.HasFlag(DumpFormats.Plaintext)) writer.WriteString("plainCode", value.PlainCode);
                if (formats.HasFlag(DumpFormats.Graphviz)) writer.WriteString("graphvizCode", value.GraphvizCode);
                writer.WriteEndObject();
            }
        }
    }
}