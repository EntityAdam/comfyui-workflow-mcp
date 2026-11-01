using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace ComfyUi.WorkflowMcp;

public sealed partial class WorkflowConfigurationManager(IConfiguration configuration)
{
    public string RootDirectory => configuration.Directory();
    public string SettingsPath => Path.Combine(RootDirectory, "settings.yaml");

    public WorkflowValidation Load(string workflowId)
    {
        var path = WorkflowConfigPath(workflowId);
        var root = LoadDocument(path);
        var defaults = Object(root["defaults"]);
        // Accept preview5 files while setup rewrites them to the simplified schema.
        if (root["model"] is { } legacyModel && defaults["model"] is null)
            defaults["model"] = legacyModel.DeepClone();
        var allowed = Object(root["allowed_values"]);
        var constraints = Object(root["constraints"]);
        return new WorkflowValidation(
            workflowId,
            root["media_type"]?.ToString(),
            root["wait_for_completion"]?.GetValue<bool?>(),
            defaults,
            allowed,
            constraints,
            string.IsNullOrWhiteSpace(root["description"]?.ToString()) ? null : root["description"]!.ToString());
    }

    public string? DefaultWorkflow(string mediaType) =>
        Object(LoadDocument(SettingsPath)["default_workflows"])[Normalize(mediaType)]?.ToString();

    public object Status(WorkflowManager workflows)
    {
        var defaults = Object(LoadDocument(SettingsPath)["default_workflows"]);
        return new
        {
            workflow_directory = workflows.DirectoryPath,
            directory = RootDirectory,
            default_workflows = defaults,
            workflows = workflows.Catalog(),
            parameterization_guide = new
            {
                json_shape = "The root object is keyed by node ID. Each node normally contains class_type, inputs, and optional _meta. Inspect node.inputs for literal values to expose. An array such as [\"105:104\", 0] links another node's output and should not be parameterized as a literal.",
                common_input_names = new[] { "cfg", "steps", "width", "height", "prompt", "negative", "negative_prompt" },
                discovery = new[]
                {
                    "Call workflow_list to discover workflow IDs, then call workflow_get with the required workflow_id to retrieve its JSON.",
                    "Search every node's inputs object for common names and other values users may need to change.",
                    "Record the exact top-level node ID and exact input key. Node IDs may contain colons.",
                    "Choose a stable public parameter name and the matching type: string, integer, number, or boolean.",
                    "Call setup with operation parameterize_workflow, the workflow_id, and only the mappings to add or change. Calls are additive: existing PARAM_* bindings remain in the JSON, and multiple parameters may also be submitted together.",
                    "Call setup with configure_workflow to record media_type, defaults, allowed_values, and constraints in YAML. Updates merge by parameter name; send only new or changed entries, never repeat unchanged defaults. Constraint objects merge by rule name; arrays replace the named parameter's allowed values. A null entry removes that entry; omitted entries and empty objects preserve existing configuration. Then verify with workflow_get_options.",
                    "To remove parameterization, call setup with operation unparameterize_workflow and parameter_names, for example [\"cfg\"]. Every binding is restored to the parameter's current YAML default, and its defaults, allowed_values, and constraints are removed. All selected parameters must have valid defaults; otherwise nothing is changed. To remove only a default while keeping its placeholder, use configure_workflow with defaults: {\"cfg\": null}."
                },
                parameterize_workflow_example = new
                {
                    operation = "parameterize_workflow",
                    workflow_id = "example_workflow",
                    parameters = new Dictionary<string, object>
                    {
                        ["prompt"] = new { node_id = "6", input_name = "text", type = "string" },
                        ["negative_prompt"] = new { node_id = "7", input_name = "text", type = "string" },
                        ["cfg"] = new { node_id = "3", input_name = "cfg", type = "number" },
                        ["steps"] = new { node_id = "3", input_name = "steps", type = "integer" },
                        ["width"] = new { node_id = "5", input_name = "width", type = "integer" },
                        ["height"] = new { node_id = "5", input_name = "height", type = "integer" }
                    }
                }
            }
        };
    }

    public string SetDefaultWorkflow(string mediaType, string workflowId, WorkflowManager workflows)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        if (workflows.Load(workflowId) is null)
            throw new ArgumentException($"Workflow '{workflowId}' not found.");
        var settings = LoadDocument(SettingsPath);
        var defaults = Object(settings["default_workflows"]);
        defaults[normalizedType] = workflowId;
        settings["default_workflows"] = defaults;
        SaveDocument(SettingsPath, settings);
        return SettingsPath;
    }

    public string SaveWorkflow(
        string workflowId,
        string? mediaType,
        bool? waitForCompletion,
        JsonObject? defaults,
        JsonObject? allowedValues,
        JsonObject? constraints,
        string? description = null)
    {
        var path = WorkflowConfigPath(workflowId);
        var document = LoadDocument(path);
        // Preserve legacy model defaults when applying incremental updates.
        if (document["model"] is { } legacyModel)
        {
            var existingDefaults = Object(document["defaults"]);
            if (existingDefaults["model"] is null) existingDefaults["model"] = legacyModel.DeepClone();
            document["defaults"] = existingDefaults;
        }
        if (description is not null)
        {
            if (string.IsNullOrWhiteSpace(description)) document.Remove("description");
            else document["description"] = description;
        }
        document.Remove("family");
        document.Remove("model");
        if (!string.IsNullOrWhiteSpace(mediaType))
            document["media_type"] = NormalizeMediaType(mediaType);
        if (waitForCompletion is not null)
            document["wait_for_completion"] = waitForCompletion.Value;
        MergeSection(document, "defaults", defaults);
        MergeSection(document, "allowed_values", allowedValues);
        MergeSection(document, "constraints", constraints, recursive: true);
        SaveDocument(path, document);
        return path;
    }

    public string ParameterizeWorkflow(
        string workflowId,
        JsonObject parameters,
        WorkflowManager workflows)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        foreach (var (parameterName, specificationNode) in parameters)
        {
            var specification = specificationNode as JsonObject
                ?? throw new ArgumentException($"Parameter '{parameterName}' must be an object.");
            var nodeId = specification["node_id"]?.ToString()
                ?? throw new ArgumentException($"Parameter '{parameterName}' requires node_id.");
            var inputName = specification["input_name"]?.ToString()
                ?? throw new ArgumentException($"Parameter '{parameterName}' requires input_name.");
            var type = specification["type"]?.ToString() ?? "string";
            if (workflow[nodeId]?["inputs"] is not JsonObject inputs || !inputs.ContainsKey(inputName))
                throw new ArgumentException($"Workflow input '{nodeId}.{inputName}' was not found.");
            inputs[inputName] = Placeholder(parameterName, type);
        }
        var path = Path.Combine(workflows.DirectoryPath, SafeName(workflowId) + ".json");
        File.WriteAllText(path, workflow.ToJsonString(new() { WriteIndented = true }));
        return path;
    }

    public string UnparameterizeWorkflow(
        string workflowId,
        IReadOnlyList<string> parameterNames,
        WorkflowManager workflows)
    {
        if (parameterNames.Count == 0)
            throw new ArgumentException("parameter_names must contain at least one parameter.");
        var workflow = workflows.Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        var parameters = workflows.ExtractParameters(workflow);
        var validation = Load(workflowId);
        var names = parameterNames.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in names)
        {
            if (!parameters.TryGetValue(name, out var parameter))
                throw new ArgumentException($"Workflow '{workflowId}' has no parameter '{name}'.");
            var value = validation.Defaults[name] as JsonValue
                ?? throw new ArgumentException($"Parameter '{name}' requires a scalar YAML default before removing parameterization. Set it with configure_workflow first.");
            ValidateValue(name, value, validation);
            WorkflowManager.RestoreParameterDefault(workflow, parameter, value);
        }
        var remaining = workflows.ExtractParameters(workflow);
        if (names.Any(remaining.ContainsKey))
            throw new ArgumentException("A selected default contains a parameter placeholder. Supply a literal default before removing parameterization.");

        var configPath = WorkflowConfigPath(workflowId);
        var document = LoadDocument(configPath);
        foreach (var name in names)
        {
            foreach (var section in new[] { "defaults", "allowed_values", "constraints" })
                if (document[section] is JsonObject values) values.Remove(name);
            if (name == "model") document.Remove("model");
        }
        var path = Path.Combine(workflows.DirectoryPath, SafeName(workflowId) + ".json");
        var originalWorkflow = File.ReadAllText(path);
        // Validate all selected parameters before writing either file.
        File.WriteAllText(path, workflow.ToJsonString(new() { WriteIndented = true }));
        try { SaveDocument(configPath, document); }
        catch
        {
            File.WriteAllText(path, originalWorkflow);
            throw;
        }
        return path;
    }

    private static void MergeSection(JsonObject document, string name, JsonObject? changes, bool recursive = false)
    {
        if (changes is null || changes.Count == 0) return;
        var values = Object(document[name]);
        foreach (var (key, value) in changes)
        {
            if (value is null) values.Remove(key);
            else if (recursive && value is JsonObject rules)
                MergeSection(values, key, rules, recursive: true);
            else values[key] = value.DeepClone();
        }
        document[name] = values;
    }

    public void ValidateValue(string name, JsonNode value, WorkflowValidation validation)
    {
        if (validation.AllowedValues[name] is JsonArray allowed && allowed.Count > 0 &&
            !allowed.Any(item => string.Equals(item?.ToString(), value.ToString(), StringComparison.Ordinal)))
            throw new GenerationParameterException(
                name,
                $"Unsupported value '{value}' for '{name}'.",
                allowed.Select(item => item?.ToString()).OfType<string>().ToArray());

        if (validation.Constraints[name] is not JsonObject rule) return;
        if (rule["minimum"] is { } minimum && TryNumber(value, out var numeric) &&
            TryNumber(minimum, out var min) && numeric < min)
            throw new GenerationParameterException(name, $"'{name}' must be at least {min}.");
        if (rule["maximum"] is { } maximum && TryNumber(value, out numeric) &&
            TryNumber(maximum, out var max) && numeric > max)
            throw new GenerationParameterException(name, $"'{name}' must be at most {max}.");
        if (rule["pattern"] is { } pattern &&
            !Regex.IsMatch(value.ToString(), pattern.ToString(), RegexOptions.CultureInvariant))
            throw new GenerationParameterException(name, $"'{name}' does not match the configured pattern.");
    }

    private string WorkflowConfigPath(string workflowId) =>
        Path.Combine(RootDirectory, "workflows", SafeName(workflowId) + ".yaml");

    private static string Placeholder(string name, string type)
    {
        var prefix = Normalize(type) switch
        {
            "int" or "integer" => "INT_",
            "float" or "number" => "FLOAT_",
            "bool" or "boolean" => "BOOL_",
            "string" or "str" => "STR_",
            _ => throw new ArgumentException($"Unsupported parameter type '{type}'.")
        };
        return $"PARAM_{prefix}{Normalize(name).ToUpperInvariant()}";
    }

    private static string NormalizeMediaType(string value)
    {
        var normalized = Normalize(value);
        return normalized is "image" or "audio" or "video"
            ? normalized
            : throw new ArgumentException("media_type must be image, audio, or video.");
    }

    private static string SafeName(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0 || normalized != value.ToLowerInvariant().Replace('-', '_'))
            throw new ArgumentException($"Invalid identifier '{value}'.");
        return value;
    }

    private static JsonObject LoadDocument(string path)
    {
        if (!File.Exists(path)) return [];
        var raw = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build()
            .Deserialize<object>(File.ReadAllText(path));
        return ConvertToJson(raw) as JsonObject ?? [];
    }

    private static void SaveDocument(string path, JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = ToPlainObject(document);
        File.WriteAllText(path, new SerializerBuilder().Build().Serialize(plain));
    }

    private static JsonObject Object(JsonNode? node) =>
        node is JsonObject value ? value.DeepClone().AsObject() : [];

    private static JsonNode? ConvertToJson(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => new JsonObject(map.ToDictionary(
            pair => pair.Key.ToString()!, pair => ConvertToJson(pair.Value))),
        IEnumerable<object> sequence => new JsonArray(sequence.Select(ConvertToJson).ToArray()),
        bool boolean => JsonValue.Create(boolean),
        int integer => JsonValue.Create(integer),
        long integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        string text when bool.TryParse(text, out var boolean) => JsonValue.Create(boolean),
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString())
    };

    private static object? ToPlainObject(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => ToPlainObject(pair.Value)),
        JsonArray array => array.Select(ToPlainObject).ToList(),
        JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue value when value.TryGetValue<long>(out var integer) => integer,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value => value.ToString(),
        _ => node.ToString()
    };

    private static bool TryNumber(JsonNode node, out double value) =>
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Normalize(string value) =>
        NonWordRegex().Replace(value.Trim().ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWordRegex();
}
