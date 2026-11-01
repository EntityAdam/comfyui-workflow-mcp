using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class WorkflowPreparationService(
    WorkflowManager workflows,
    WorkflowConfigurationManager configuration,
    GenerationOptionsService options,
    ComfyClient client)
{
    public WorkflowPlan Capture(string workflowId, JsonObject values)
    {
        var graph = workflows.Load(workflowId) ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        var validation = configuration.Load(workflowId);
        WorkflowTransforms.ValidateTargets(graph, validation.Transforms);
        var resolved = options.Resolve(workflowId, values, graph, validation);
        workflows.RenderResolved(graph, resolved.Values);
        if (validation.Transforms.Count > 0) ValidateGraph(graph);
        return new WorkflowPlan(workflowId, resolved.MediaType, validation.WaitForCompletion ?? true,
            graph, resolved.Values, validation.Transforms);
    }

    public Task<PreparedWorkflow> PrepareAsync(string workflowId, JsonObject values, CancellationToken cancellationToken = default) =>
        PrepareAsync(Capture(workflowId, values), cancellationToken);

    public async Task<PreparedWorkflow> PrepareAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        var graph = plan.Workflow.DeepClone().AsObject();
        WorkflowTransforms.ValidateTargets(graph, plan.Transforms);
        var changes = new JsonArray();
        // Definitions are shared within one preparation, never across ComfyUI restarts.
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        async Task<JsonObject> Definition(string classType)
        {
            if (!definitions.TryGetValue(classType, out var definition))
            {
                definition = await client.GetNodeDefinitionAsync(classType, cancellationToken);
                definitions.Add(classType, definition);
            }
            return definition;
        }

        foreach (var transform in WorkflowTransforms.Parse(plan.Transforms).Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transform.Kind == WorkflowTransforms.SeedKind)
            {
                foreach (var (nodeId, node) in graph)
                {
                    if (node?["class_type"]?.ToString() != "Seed (rgthree)" ||
                        node["inputs"]?["seed"] is not JsonValue seed ||
                        !seed.TryGetValue<long>(out var number) || number != -1) continue;
                    var definition = await Definition("SeedNode");
                    RequireOutput(definition, "SeedNode", 0, "INT");
                    var seedInput = Input(definition, "seed");
                    var seedOptions = Item(seedInput, 1) as JsonObject;
                    if (Item(seedInput, 0)?.ToString() != "INT" ||
                        seedOptions?["min"] is JsonValue minimum && minimum.TryGetValue<double>(out var min) && min > -1 ||
                        seedOptions?["max"] is JsonValue maximum && maximum.TryGetValue<double>(out var max) && max < -1)
                        throw WorkflowTransforms.Invalid("Installed SeedNode does not accept seed -1.");
                    node["class_type"] = "SeedNode";
                    node["inputs"] = new JsonObject { ["seed"] = -1 };
                    if (node["_meta"] is not JsonObject) node["_meta"] = new JsonObject();
                    node["_meta"]!["title"] = "Seed";
                    ValidateRequiredInputs(node.AsObject(), definition);
                    changes.Add(Change(transform, "replace", nodeId, "SeedNode"));
                }
            }
            else
            {
                var definition = await Definition("MiniMaxH3ReferenceToVideo");
                RequireOutput(definition, "MiniMaxH3ReferenceToVideo", 0, "CONDITIONING");
                RequireOutput(definition, "MiniMaxH3ReferenceToVideo", 1, "LATENT");
                ExpandReferences(graph, plan.Values, transform, definition, changes);
                ValidateRequiredInputs(graph[transform.NodeId!]!.AsObject(), definition);
                foreach (var node in graph.Select(pair => pair.Value).OfType<JsonObject>()
                             .Where(node => Owner(node) == OwnerKey(transform)))
                {
                    var classType = node["class_type"]!.ToString();
                    var loaderDefinition = await Definition(classType);
                    ValidateRequiredInputs(node, loaderDefinition);
                    switch (classType)
                    {
                        case "LoadImage": RequireOutput(loaderDefinition, classType, 0, "IMAGE"); break;
                        case "LoadAudio": RequireOutput(loaderDefinition, classType, 0, "AUDIO"); break;
                        case "LoadVideo": RequireOutput(loaderDefinition, classType, 0, "VIDEO"); break;
                        case "GetVideoComponents":
                            RequireOutput(loaderDefinition, classType, 0, "IMAGE");
                            RequireOutput(loaderDefinition, classType, 1, "AUDIO");
                            break;
                    }
                }
            }
        }
        if (plan.Transforms.Count > 0) ValidateGraph(graph);
        return new PreparedWorkflow(plan.WorkflowId, plan.MediaType, graph, changes);
    }

    private static void ExpandReferences(JsonObject graph, JsonObject values, WorkflowTransform transform, JsonObject definition, JsonArray changes)
    {
        var target = graph[transform.NodeId!]!.AsObject();
        var inputs = target["inputs"]!.AsObject();
        var owned = graph.Where(pair => pair.Value is JsonObject node && Owner(node) == OwnerKey(transform))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        // This transform owns the target's four reference groups, but never deletes user loaders.
        foreach (var key in inputs.Select(pair => pair.Key).Where(IsReferenceInput).ToArray()) inputs.Remove(key);
        foreach (var (nodeId, node) in graph.Where(pair => !owned.Contains(pair.Key)))
            if (Links(node).Any(link => owned.Contains(link.Source)))
                throw WorkflowTransforms.Invalid($"Cannot rebuild references: node '{nodeId}' consumes a transform-owned loader.");
        foreach (var nodeId in owned)
        {
            graph.Remove(nodeId);
            changes.Add(Change(transform, "remove", nodeId));
        }

        var images = values["reference_images"]!.AsArray();
        var videos = values["reference_videos"]!.AsArray();
        var audios = values["reference_audios"]!.AsArray();
        CheckGroup(definition, "ref_images", "ref_image_", "IMAGE", images.Count);
        CheckGroup(definition, "ref_videos", "ref_video_", "IMAGE", videos.Count);
        CheckGroup(definition, "ref_video_audios", "ref_video_audio_", "AUDIO", videos.Count,
            videos.Any(video => video!["include_audio"]?.GetValue<bool>() == true));
        CheckGroup(definition, "ref_audios", "ref_audio_", "AUDIO", audios.Count);

        string Add(string role, int index, string classType, JsonObject loaderInputs)
        {
            var stem = $"mcp:{transform.Id}:{role}:{index}";
            var id = stem;
            for (var suffix = 1; graph.ContainsKey(id); suffix++) id = $"{stem}:{suffix}";
            graph[id] = new JsonObject
            {
                ["class_type"] = classType,
                ["inputs"] = loaderInputs,
                ["_meta"] = new JsonObject { ["title"] = classType, ["workflow_mcp_transform"] = OwnerKey(transform) }
            };
            changes.Add(Change(transform, "add", id, classType));
            return id;
        }

        for (var i = 0; i < images.Count; i++)
        {
            var id = Add("image", i, "LoadImage", new JsonObject { ["image"] = images[i]!.DeepClone() });
            inputs[$"ref_images.ref_image_{i}"] = Link(id, 0);
        }
        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i]!.AsObject();
            var id = Add("video", i, "LoadVideo", new JsonObject { ["file"] = video["file"]!.DeepClone() });
            var components = Add("components", i, "GetVideoComponents", new JsonObject { ["video"] = Link(id, 0) });
            inputs[$"ref_videos.ref_video_{i}"] = Link(components, 0);
            if (video["include_audio"]?.GetValue<bool>() == true)
                inputs[$"ref_video_audios.ref_video_audio_{i}"] = Link(components, 1);
        }
        for (var i = 0; i < audios.Count; i++)
        {
            var id = Add("audio", i, "LoadAudio", new JsonObject { ["audio"] = audios[i]!.DeepClone() });
            inputs[$"ref_audios.ref_audio_{i}"] = Link(id, 0);
        }
        changes.Add(Change(transform, "bind_references", transform.NodeId!));
    }

    private static void CheckGroup(JsonObject definition, string group, string prefix, string type, int count, bool used = true)
    {
        var input = Input(definition, group);
        var template = (Item(input, 1) as JsonObject)?["template"] as JsonObject;
        var templateInput = template?["input"] as JsonObject;
        var socket = templateInput?["required"]?.AsObject().FirstOrDefault().Value as JsonArray
            ?? templateInput?["optional"]?.AsObject().FirstOrDefault().Value as JsonArray;
        if (Item(input, 0)?.ToString() != "COMFY_AUTOGROW_V3" || template?["prefix"]?.ToString() != prefix || Item(socket, 0)?.ToString() != type)
            throw WorkflowTransforms.Invalid($"Installed MiniMax reference group '{group}' has an unsupported schema.");
        if (used && (template!["max"] is not JsonValue maximum || !maximum.TryGetValue<int>(out var max) || count > max))
            throw WorkflowTransforms.Invalid($"Reference list exceeds the installed '{group}' slot limit.");
    }

    private static JsonArray? Input(JsonObject definition, string name) =>
        definition["input"]?["required"]?[name] as JsonArray ?? definition["input"]?["optional"]?[name] as JsonArray;

    private static JsonNode? Item(JsonArray? array, int index) => array is not null && array.Count > index ? array[index] : null;

    private static void RequireOutput(JsonObject definition, string classType, int slot, string type)
    {
        if (definition["output"] is not JsonArray outputs || outputs.Count <= slot || outputs[slot]?.ToString() != type)
            throw WorkflowTransforms.Invalid($"Installed '{classType}' must provide {type} at output {slot}.");
    }

    private static void ValidateRequiredInputs(JsonObject node, JsonObject definition)
    {
        if (definition["input"]?["required"] is not JsonObject required) return;
        foreach (var (name, input) in required)
            if (input is JsonArray spec && Item(spec, 0)?.ToString() != "COMFY_AUTOGROW_V3" && node["inputs"]?[name] is null)
                throw WorkflowTransforms.Invalid($"'{node["class_type"]}' is missing required input '{name}'.");
    }

    private static string OwnerKey(WorkflowTransform transform) => $"{transform.Id}/{transform.NodeId}";
    private static string? Owner(JsonObject node) => WorkflowTransforms.String(node["_meta"]?["workflow_mcp_transform"]);
    private static bool IsReferenceInput(string key) => key is "ref_images" or "ref_videos" or "ref_video_audios" or "ref_audios" ||
        key.StartsWith("ref_images.", StringComparison.Ordinal) || key.StartsWith("ref_videos.", StringComparison.Ordinal) ||
        key.StartsWith("ref_video_audios.", StringComparison.Ordinal) || key.StartsWith("ref_audios.", StringComparison.Ordinal);
    private static JsonArray Link(string id, int slot) => new(id, slot);
    private static JsonObject Change(WorkflowTransform transform, string operation, string nodeId, string? classType = null) => new()
    {
        ["transform_id"] = transform.Id,
        ["operation"] = operation,
        ["node_id"] = nodeId,
        ["class_type"] = classType
    };

    private static IEnumerable<(string Source, int Slot)> Links(JsonNode? node)
    {
        if (node?["inputs"] is not JsonObject inputs) yield break;
        foreach (var value in inputs.Select(pair => pair.Value))
            if (value is JsonArray { Count: 2 } link && WorkflowTransforms.String(link[0]) is { } source &&
                link[1] is JsonValue slot && slot.TryGetValue<int>(out var index)) yield return (source, index);
    }

    internal static void ValidateGraph(JsonObject graph)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw WorkflowTransforms.Invalid($"Prepared workflow contains a cycle at '{id}'.");
            foreach (var link in Links(graph[id]))
            {
                if (!graph.ContainsKey(link.Source) || link.Slot < 0)
                    throw WorkflowTransforms.Invalid($"Node '{id}' has an invalid connection to '{link.Source}' output {link.Slot}.");
                Visit(link.Source);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in graph.Select(pair => pair.Key)) Visit(id);
    }
}
