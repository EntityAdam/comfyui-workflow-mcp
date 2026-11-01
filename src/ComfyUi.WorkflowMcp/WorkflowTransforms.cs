using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

internal sealed record WorkflowTransform(string Id, string Kind, string? NodeId, bool Enabled);

internal static class WorkflowTransforms
{
    internal const string SeedKind = "replace_negative_rgthree_seed";
    internal const string ReferenceKind = "minimax_references";
    internal static readonly string[] MediaParameters = ["reference_images", "reference_videos", "reference_audios"];

    internal static IReadOnlyList<WorkflowTransform> Parse(JsonArray transforms)
    {
        var result = new List<WorkflowTransform>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in transforms)
        {
            if (item is not JsonObject entry || entry.Any(pair => pair.Key is not ("id" or "kind" or "node_id" or "enabled")))
                throw Invalid("Each transform needs id, kind, and optionally node_id and enabled.");
            var id = String(entry["id"]);
            var kind = String(entry["kind"]);
            var nodeId = String(entry["node_id"]);
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw Invalid("Transform IDs must be nonempty and unique.");
            if (kind is not (SeedKind or ReferenceKind))
                throw Invalid($"Unknown transform kind '{kind}'.");
            if (entry.ContainsKey("enabled") && (entry["enabled"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
                throw Invalid($"Transform '{id}' enabled must be a boolean.");
            if (kind == ReferenceKind && string.IsNullOrWhiteSpace(nodeId))
                throw Invalid($"Transform '{id}' requires node_id.");
            if (kind == SeedKind && entry.ContainsKey("node_id"))
                throw Invalid($"Transform '{id}' matches all rgthree seed nodes; node_id is not supported.");
            result.Add(new WorkflowTransform(id, kind, nodeId, entry["enabled"]?.GetValue<bool>() ?? true));
        }
        if (result.Count(item => item.Enabled && item.Kind == ReferenceKind) > 1)
            throw Invalid("Only one enabled minimax_references transform is supported per workflow.");
        return result;
    }

    internal static bool HasReferences(JsonArray transforms) =>
        Parse(transforms).Any(item => item.Enabled && item.Kind == ReferenceKind);

    internal static void ValidateTargets(JsonObject workflow, JsonArray transforms)
    {
        foreach (var transform in Parse(transforms).Where(item => item.Enabled && item.Kind == ReferenceKind))
            if (workflow[transform.NodeId!]?["class_type"]?.ToString() != "MiniMaxH3ReferenceToVideo" ||
                workflow[transform.NodeId!]?["inputs"] is not JsonObject)
                throw Invalid($"Transform '{transform.Id}' must target a MiniMaxH3ReferenceToVideo node with inputs, found '{transform.NodeId}'.");
    }

    internal static JsonObject ParameterSchemas() => new()
    {
        ["reference_images"] = FileListSchema(9),
        ["reference_audios"] = FileListSchema(3),
        ["reference_videos"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = 3,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("file"),
                ["properties"] = new JsonObject
                {
                    ["file"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                    ["include_audio"] = new JsonObject { ["type"] = "boolean", ["default"] = false }
                }
            }
        }
    };

    private static JsonObject FileListSchema(int maximum) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maximum,
        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
    };

    internal static void ValidateMedia(string name, JsonNode? value)
    {
        var maximum = name == "reference_images" ? 9 : 3;
        if (value is not JsonArray array || array.Count > maximum)
            throw new GenerationParameterException(name, $"'{name}' must be an ordered array of at most {maximum} references.");
        foreach (var item in array)
        {
            if (name != "reference_videos")
            {
                if (string.IsNullOrWhiteSpace(String(item)))
                    throw new GenerationParameterException(name, $"Each '{name}' entry must be a nonempty ComfyUI input filename.");
                continue;
            }
            if (item is not JsonObject video || string.IsNullOrWhiteSpace(String(video["file"])) ||
                video.Any(pair => pair.Key is not ("file" or "include_audio")) ||
                (video.ContainsKey("include_audio") && (video["include_audio"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))))
                throw new GenerationParameterException(name, "Each video needs a nonempty file and an optional boolean include_audio.");
        }
    }

    internal static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static ToolFailureException Invalid(string message) => new("invalid_transform", message,
        "Read workflow_get_options and correct the transforms in workflow_configure or workflow YAML.");
}
