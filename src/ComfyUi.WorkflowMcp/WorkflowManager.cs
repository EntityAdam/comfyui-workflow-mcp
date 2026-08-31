using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;

namespace ComfyUi.WorkflowMcp;

public sealed partial class WorkflowManager(IConfiguration configuration, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public string DirectoryPath => Path.GetFullPath(configuration.WorkflowDirectory());

    public IReadOnlyList<WorkflowCatalogEntry> Catalog()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var catalog = new List<WorkflowCatalogEntry>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")
                     .Order(StringComparer.Ordinal)
                     .Where(p => !p.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var workflow = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                var parameters = ExtractParameters(workflow);
                var id = Path.GetFileNameWithoutExtension(path);
                catalog.Add(new WorkflowCatalogEntry(
                    id,
                    id.Replace('_', ' '),
                    $"Execute the '{id}' workflow.",
                    parameters.ToDictionary(
                        x => x.Key,
                        x => (object)new { type = TypeName(x.Value.Type), description = x.Value.Description })));
            }
            catch (JsonException)
            {
                // Match the Python server: malformed workflow files are omitted from discovery.
            }
        }
        return catalog;
    }

    public JsonObject? Load(string workflowId)
    {
        var safe = SafeId(workflowId);
        if (safe is null) return null;
        var path = Path.Combine(DirectoryPath, safe + ".json");
        if (!File.Exists(path) || !PathSafety.IsWithin(path, DirectoryPath)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
        catch (JsonException) { return null; }
    }

    public JsonObject RenderResolved(string workflowId, JsonObject values)
    {
        var workflow = Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        ApplyDatePlaceholders(workflow);
        var parameters = ExtractParameters(workflow);
        foreach (var parameter in parameters.Values)
        {
            var value = values[parameter.Name]
                ?? throw new ArgumentException(
                    $"No resolved value exists for parameter '{parameter.Name}'.");
            var coerced = Coerce(value, parameter.Type);
            foreach (var binding in parameter.Bindings)
                ApplyBinding(workflow, binding, coerced);
        }
        return workflow;
    }

    public IReadOnlyDictionary<string, WorkflowParameter> ExtractParameters(JsonObject workflow)
    {
        var result = new Dictionary<string, WorkflowParameter>();
        foreach (var (nodeId, node) in workflow)
        {
            if (node is not JsonObject nodeObject || nodeObject["inputs"] is not JsonObject inputs) continue;
            foreach (var (inputName, input) in inputs)
            {
                if (input is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                if (text.StartsWith("PARAM_", StringComparison.Ordinal) &&
                    !text.StartsWith("PARAM_DATE_", StringComparison.Ordinal) &&
                    FullPlaceholderRegex().IsMatch(text))
                {
                    AddParameter(result, ParsePlaceholder(text), nodeId, inputName);
                    continue;
                }
                foreach (Match match in EmbeddedPlaceholderRegex().Matches(text))
                    AddParameter(
                        result,
                        ParsePlaceholder(match.Value),
                        nodeId,
                        inputName,
                        match.Value);
            }
        }
        return result;
    }

    private static void AddParameter(
        Dictionary<string, WorkflowParameter> parameters,
        (string Name, Type Type) parsed,
        string nodeId,
        string inputName,
        string? token = null)
    {
        if (!parameters.TryGetValue(parsed.Name, out var parameter))
        {
            parameter = new WorkflowParameter(
                parsed.Name,
                parsed.Type,
                $"Value for '{parsed.Name}'.",
                []);
            parameters[parsed.Name] = parameter;
        }
        parameter.Bindings.Add(new WorkflowBinding(nodeId, inputName, token));
    }

    private static void ApplyBinding(
        JsonObject workflow,
        WorkflowBinding binding,
        JsonNode value)
    {
        var inputs = workflow[binding.NodeId]!["inputs"]!;
        if (binding.Token is null)
        {
            inputs[binding.InputName] = value.DeepClone();
            return;
        }
        var template = inputs[binding.InputName]?.ToString()
            ?? throw new InvalidOperationException(
                $"Embedded placeholder input '{binding.NodeId}.{binding.InputName}' is missing.");
        inputs[binding.InputName] = template.Replace(
            binding.Token, value.ToString(), StringComparison.Ordinal);
    }

    private void ApplyDatePlaceholders(JsonObject workflow)
    {
        var now = _clock.GetLocalNow();
        foreach (var (_, node) in workflow)
        {
            if (node is not JsonObject nodeObject || nodeObject["inputs"] is not JsonObject inputs)
                continue;
            foreach (var inputName in inputs.Select(input => input.Key).ToArray())
            {
                if (inputs[inputName] is not JsonValue value ||
                    !value.TryGetValue<string>(out var text) ||
                    !text.Contains("PARAM_DATE_", StringComparison.Ordinal)) continue;
                inputs[inputName] = DatePlaceholderRegex().Replace(
                    text,
                    match => FormatDate(now, match.Groups[1].Value));
            }
        }
    }

    private static string FormatDate(DateTimeOffset value, string tokenFormat)
    {
        if (!DateFormatRegex().IsMatch(tokenFormat))
            throw new ArgumentException(
                $"Unsupported PARAM_DATE format '{tokenFormat}'.");
        var format = tokenFormat
            .Replace("YYYY", "yyyy", StringComparison.Ordinal)
            .Replace("YY", "yy", StringComparison.Ordinal)
            .Replace("DD", "dd", StringComparison.Ordinal);
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static (string Name, Type Type) ParsePlaceholder(string placeholder)
    {
        var token = placeholder["PARAM_".Length..];
        var type = typeof(string);
        foreach (var pair in new[]
                 {
                     ("INT_", typeof(long)),
                     ("FLOAT_", typeof(double)),
                     ("FLT_", typeof(double)),
                     ("BOOL_", typeof(bool)),
                     ("STR_", typeof(string))
                 })
            if (token.StartsWith(pair.Item1, StringComparison.OrdinalIgnoreCase))
            {
                type = pair.Item2;
                token = token[pair.Item1.Length..];
                break;
            }
        return (Normalize(token), type);
    }

    private static JsonValue Coerce(JsonNode value, Type type)
    {
        var text = value.ToString();
        if (type == typeof(long) && long.TryParse(text, out var l)) return JsonValue.Create(l)!;
        if (type == typeof(double) && double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d)!;
        if (type == typeof(bool) && bool.TryParse(text, out var b)) return JsonValue.Create(b)!;
        if (type == typeof(string)) return JsonValue.Create(text)!;
        throw new ArgumentException($"Cannot convert '{text}' to {TypeName(type)}.");
    }

    private static string? SafeId(string id)
    {
        var normalized = Normalize(id);
        return normalized.Length == 0 || normalized != id.ToLowerInvariant().Replace('-', '_') ? null : id;
    }

    private static string TypeName(Type t) => t == typeof(long) ? "int" : t == typeof(double) ? "float" : t == typeof(bool) ? "bool" : "str";

    private static string Normalize(string text) =>
        NonWordRegex().Replace(text.Trim().ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWordRegex();
    [GeneratedRegex("^PARAM_(?!DATE_)(?:(?:INT|FLOAT|FLT|BOOL|STR)_)?[A-Z][A-Z0-9_]*$")]
    private static partial Regex FullPlaceholderRegex();
    [GeneratedRegex("PARAM_(?!DATE_)(?:(?:INT|FLOAT|FLT|BOOL|STR)_)?[A-Z][A-Z0-9_]*")]
    private static partial Regex EmbeddedPlaceholderRegex();
    [GeneratedRegex("PARAM_DATE_([A-Za-z0-9_-]+)")]
    private static partial Regex DatePlaceholderRegex();
    [GeneratedRegex("^[YMDHhms_-]+$")]
    private static partial Regex DateFormatRegex();
}
