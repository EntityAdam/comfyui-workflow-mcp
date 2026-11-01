using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUi.WorkflowMcp;

[JsonConverter(typeof(WorkflowParameterTypeConverter))]
public enum WorkflowParameterType
{
    [JsonStringEnumMemberName("string")] Text,
    [JsonStringEnumMemberName("integer")] WholeNumber,
    [JsonStringEnumMemberName("number")] DecimalNumber,
    [JsonStringEnumMemberName("boolean")] Flag
}

public sealed class WorkflowParameterTypeConverter() : JsonStringEnumConverter<WorkflowParameterType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

public sealed class WorkflowParameterMapping
{
    [JsonPropertyName("node_id"), Description("Exact top-level node ID, including colons.")]
    public required string NodeId { get; init; }

    [JsonPropertyName("input_name"), Description("Exact key under the node's inputs.")]
    public required string InputName { get; init; }

    [JsonPropertyName("type"), JsonConverter(typeof(WorkflowParameterTypeConverter))]
    public required WorkflowParameterType Type { get; init; }
}

public sealed record WorkflowParameter(string Name, Type Type, string Description, List<WorkflowBinding> Bindings);
public sealed record WorkflowBinding(string NodeId, string InputName, string? Token = null);
public sealed record WorkflowCatalogEntry(
    string Id,
    string Name,
    string Description,
    IReadOnlyDictionary<string, object> AvailableInputs);
public sealed record ComfySubmissionResult(string PromptId, int? QueueNumber, JsonObject SubmittedWorkflow);
public sealed record ComfyOutputAsset(string Filename, string Subfolder, string FolderType);
public sealed record ComfyInputUpload(string Name, string Subfolder, string Type)
{
    public string WorkflowValue => string.IsNullOrWhiteSpace(Subfolder)
        ? Name
        : $"{Subfolder.TrimEnd('/', '\\').Replace('\\', '/')}/{Name}";
}
public sealed record WorkflowValidation(string WorkflowId, string? MediaType, bool? WaitForCompletion, JsonObject Defaults, JsonObject AllowedValues, JsonObject Constraints, string? Description = null);
public sealed record ResolvedWorkflowExecution(string WorkflowId, string? MediaType, JsonObject Values);
public sealed record CompletedWorkflow(string PromptId, ComfyOutputAsset Output, byte[] Bytes, string MimeType);
