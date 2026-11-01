using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

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
