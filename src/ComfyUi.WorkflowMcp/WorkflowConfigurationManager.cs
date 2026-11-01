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
        return ReadConfiguration(workflowId, root);
    }

    private static WorkflowValidation ReadConfiguration(string workflowId, JsonObject root)
    {
        foreach (var section in new[] { "defaults", "allowed_values", "constraints" })
            if (root[section] is not null and not JsonObject)
                throw InvalidConfiguration(null, $"{section} must be an object keyed by parameter name.");
        if (root["wait_for_completion"] is { } completion && (completion is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw InvalidConfiguration(null, "wait_for_completion must be a boolean.");
        if (root["media_type"] is { } mediaType) NormalizeMediaType(mediaType.ToString());
        var defaults = Object(root["defaults"]);
        // Accept preview5 files while configuration updates migrate their schema.
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
            string.IsNullOrWhiteSpace(root["description"]?.ToString()) ? null : root["description"]!.ToString())
        {
            Transforms = ReadTransforms(root)
        };
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
            workflow_count = workflows.Catalog().Count,
            batch_guide = "Generation queues held runs by default, persisting workflow/options snapshots under batch/queue.json without submitting to ComfyUI. Use batch_count to see how many runs await Play, and batch_remove or batch_clear to edit. Entry IDs come from generation or playback receipts. Call batch_play ONLY when the user explicitly asks to play/start the batch; never play automatically. New additions during playback remain held. enqueue=false is available only for explicitly requested immediate generation. ComfyUI queue tools manage already-submitted jobs, not the held batch.",
            asset_guide = "For existing media, call asset_list and pass its filename to asset_view or comfy_media_upload. asset_stage is only for bytes supplied directly by the client; never generate or reproduce base64.",
            transform_guide = "workflow_configure accepts an ordered transforms array, replacing the full list ([] clears it). Each entry has a unique id and kind, plus optional enabled. replace_negative_rgthree_seed swaps Seed (rgthree) nodes with seed -1 for installed SeedNode nodes. minimax_references requires node_id targeting MiniMaxH3ReferenceToVideo and exposes ordered reference_images/reference_audios filename lists and reference_videos objects with file and optional include_audio (default false). Upload assets first and use workflow_value. workflow_get_options exposes their schemas; workflow_preview prepares the graph without submission. Generation applies transforms to an in-memory copy in both completion modes.",
            parameterization_guide = new
            {
                json_shape = "The root object is keyed by node ID. Each node normally contains class_type, inputs, and optional _meta. Inspect node.inputs for literal values to expose. An array such as [\"105:104\", 0] links another node's output and should not be parameterized as a literal.",
                common_input_names = new[] { "cfg", "steps", "width", "height", "prompt", "negative", "negative_prompt" },
                discovery = new[]
                {
                    "Call workflow_list for IDs and summaries, workflow_get_options for one workflow's parameters and configuration, or workflow_get for its JSON.",
                    "Search every node's inputs object for common names and other values users may need to change.",
                    "Record the exact top-level node ID and exact input key. Node IDs may contain colons.",
                    "Choose a stable public parameter name and the matching type: string, integer, number, or boolean. Use workflow_set_default to select a default for a media type.",
                    "Call workflow_parameterize with workflow_id and only new or changed parameter mappings. Existing bindings remain unchanged.",
                    "Call workflow_configure to set media_type, defaults, allowed_values, constraints, and description. Updates merge: send only changes. Omitted entries stay unchanged; null removes an entry. Constraints merge by rule; allowed-value arrays replace the named list.",
                    "Call workflow_unparameterize with parameter_names, for example [\"cfg\"], to restore current YAML defaults as fixed inputs and remove their configuration. Valid defaults are required. To keep the binding but remove its default, call workflow_configure with defaults: {\"cfg\": null}."
                },
                parameterize_example = new
                {
                    tool = "workflow_parameterize",
                    arguments = new
                    {
                        workflow_id = "example_workflow",
                        parameters = new Dictionary<string, object>
                        {
                            ["cfg"] = new { node_id = "3", input_name = "cfg", type = "number" }
                        }
                    }
                }
            }
        };
    }

    public string SetDefaultWorkflow(string mediaType, string workflowId, WorkflowManager workflows)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        if (workflows.Load(workflowId) is null)
            throw ToolFailureException.WorkflowNotFound(workflowId);
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
        string? description = null,
        JsonArray? transforms = null)
    {
        var path = WorkflowConfigPath(workflowId);
        var document = LoadDocument(path);
        if (transforms is not null)
        {
            WorkflowTransforms.Parse(transforms);
            document["transforms"] = transforms.DeepClone();
        }
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
        var workflow = WorkflowManager.LoadFromDirectory(configuration.WorkflowDirectory(), workflowId)
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        ValidateConfiguration(workflow, ReadConfiguration(workflowId, document));
        SaveDocument(path, document);
        return path;
    }

    public string ParameterizeWorkflow(
        string workflowId,
        JsonObject parameters,
        WorkflowManager workflows)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        foreach (var (parameterName, specificationNode) in parameters)
        {
            var normalizedName = Normalize(parameterName);
            if (normalizedName.Length == 0 || !char.IsAsciiLetter(normalizedName[0]))
                throw new ArgumentException($"Parameter '{parameterName}' must start with a letter after normalization.");
            var specification = specificationNode as JsonObject
                ?? throw new ArgumentException($"Parameter '{parameterName}' must be an object.");
            var nodeId = specification["node_id"]?.ToString()
                ?? throw new ArgumentException($"Parameter '{parameterName}' requires node_id.");
            var inputName = specification["input_name"]?.ToString()
                ?? throw new ArgumentException($"Parameter '{parameterName}' requires input_name.");
            var type = specification["type"]?.ToString() ?? "string";
            if (workflow[nodeId]?["inputs"] is not JsonObject inputs || !inputs.ContainsKey(inputName))
                throw new ToolFailureException("binding_not_found", $"Workflow input '{nodeId}.{inputName}' was not found.", "Read workflow_get and use an existing node_id and input_name.", parameterName);
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
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        var parameters = workflows.ExtractParameters(workflow);
        var validation = Load(workflowId);
        var names = parameterNames.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in names)
        {
            if (!parameters.TryGetValue(name, out var parameter))
                throw ToolFailureException.UnknownParameter(workflowId, name);
            var defaultValue = validation.Defaults[name] ?? throw ToolFailureException.MissingDefault(name);
            var value = defaultValue as JsonValue
                ?? throw new GenerationParameterException(name, $"Default for '{name}' must be a scalar value.", code: "invalid_default", nextStep: $"Set a scalar defaults.{name} with workflow_configure.");
            try
            {
                ValidateValue(name, value, validation);
                WorkflowManager.RestoreParameterDefault(workflow, parameter, value);
            }
            catch (ArgumentException ex)
            {
                throw new GenerationParameterException(name, ex.Message, (ex as GenerationParameterException)?.AllowedValues,
                    "invalid_default", $"Set a valid defaults.{name} with workflow_configure.");
            }
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

    private static JsonArray ReadTransforms(JsonObject document)
    {
        if (document["transforms"] is null) return [];
        if (document["transforms"] is not JsonArray transforms)
            throw WorkflowTransforms.Invalid("transforms must be an ordered array.");
        WorkflowTransforms.Parse(transforms);
        return transforms.DeepClone().AsArray();
    }

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
            : throw new ToolFailureException("invalid_media_type", "media_type must be image, audio, or video.", "Use image, audio, or video for media_type.");
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
        File.WriteAllText(path, new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(plain));
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
        byte or sbyte or short or ushort or uint => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong integer => integer <= long.MaxValue ? JsonValue.Create((long)integer) : JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        // The deserializer already types unquoted scalars. Quoted node IDs and filenames stay strings.
        _ => JsonValue.Create(value.ToString())
    };

    private static object? ToPlainObject(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => ToPlainObject(pair.Value)),
        JsonArray array => array.Select(ToPlainObject).ToList(),
        JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue value when value.TryGetValue<int>(out var integer) => integer,
        JsonValue value when value.TryGetValue<long>(out var integer) => integer,
        JsonValue value when value.TryGetValue<ulong>(out var integer) => integer,
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
