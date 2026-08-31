using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ComfyUi.WorkflowMcp;

[McpServerToolType]
public sealed class ComfyTools(
    IConfiguration configuration,
    ComfyClient client,
    WorkflowManager workflows,
    WorkflowConfigurationManager workflowConfiguration,
    GenerationOptionsService generationOptions,
    WorkflowExecutionService execution)
{
    [McpServerTool(Name = "setup"), Description("Inspect or modify ComfyUI Workflow MCP workflow setup, workflow YAML validation, media defaults, and PARAM_* bindings.")]
    public object Setup(
        [Description("Operation: status, set_workflow_directory, configure_workflow, parameterize_workflow, or set_default_workflow.")] string operation = "status",
        string? workflow_directory = null,
        string? workflow_id = null,
        [Description("image, audio, or video.")] string? media_type = null,
        [Description("Parameter mapping: each key maps to node_id, input_name, and type.")] JsonObject? parameters = null,
        JsonObject? defaults = null,
        JsonObject? allowed_values = null,
        [Description("Per-parameter validation including minimum, maximum, and pattern.")] JsonObject? constraints = null)
    {
        try
        {
            return operation switch
            {
                "status" => workflowConfiguration.Status(workflows),
                "set_workflow_directory" => new
                {
                    success = true,
                    workflow_directory = workflowConfiguration.SetWorkflowDirectory(Required(workflow_directory, nameof(workflow_directory))),
                    restart_required = false
                },
                "configure_workflow" => new
                {
                    success = true,
                    path = ConfigureWorkflow(Required(workflow_id, nameof(workflow_id)), media_type, defaults, allowed_values, constraints)
                },
                "parameterize_workflow" => new
                {
                    success = true,
                    path = workflowConfiguration.ParameterizeWorkflow(Required(workflow_id, nameof(workflow_id)), parameters ?? throw new ArgumentException("parameters is required."), workflows)
                },
                "set_default_workflow" => new
                {
                    success = true,
                    path = workflowConfiguration.SetDefaultWorkflow(Required(media_type, nameof(media_type)), Required(workflow_id, nameof(workflow_id)), workflows)
                },
                _ => throw new ArgumentException($"Unsupported setup operation '{operation}'.")
            };
        }
        catch (Exception ex) { return new { success = false, error = ex.Message }; }
    }

    [McpServerTool(Name = "workflow_list"), Description("List saved ComfyUI workflows, media types, placeholders, defaults, and validation constraints.")]
    public object WorkflowList()
    {
        var catalog = workflows.Catalog()
            .Select(item => new { workflow = item, validation = generationOptions.GetOptions(item.Id) })
            .ToArray();
        return new { workflows = catalog, count = catalog.Length, workflow_directory = workflows.DirectoryPath };
    }

    [McpServerTool(Name = "generate_image"), Description("Run an image workflow, wait for completion, and display its first image output.")]
    public Task<CallToolResult> GenerateImage(
        string prompt,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("Wait for the first image output when true; submit to ComfyUI and return immediately when false.")] bool wait_for_completion = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["prompt"] = prompt;
        return RunGenerated("image", workflow_id, values, wait_for_completion, cancellationToken);
    }

    [McpServerTool(Name = "generate_audio"), Description("Run an audio workflow, wait for completion, and return its first audio output.")]
    public Task<CallToolResult> GenerateAudio(
        string tags,
        string lyrics,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("Wait for the first audio output when true; submit to ComfyUI and return immediately when false.")] bool wait_for_completion = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["tags"] = tags;
        values["lyrics"] = lyrics;
        return RunGenerated("audio", workflow_id, values, wait_for_completion, cancellationToken);
    }

    [McpServerTool(Name = "generate_video"), Description("Run a video workflow, wait for completion, and return its first video output.")]
    public Task<CallToolResult> GenerateVideo(
        string prompt,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("Wait for the first video output when true; submit to ComfyUI and return immediately when false.")] bool wait_for_completion = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["prompt"] = prompt;
        return RunGenerated("video", workflow_id, values, wait_for_completion, cancellationToken);
    }

    [McpServerTool(Name = "comfy_media_upload"), Description("Upload a server-accessible image, audio, or video file through ComfyUI's upload endpoint.")]
    public async Task<object> ComfyMediaUpload(string file_path, string? filename = null, string? subfolder = "comfyui-workflow-mcp", bool overwrite = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var inputRoot = configuration.InputRoot();
            var path = Path.GetFullPath(file_path);
            if (!PathSafety.IsWithin(path, inputRoot) || !File.Exists(path))
                throw new ArgumentException("file_path does not exist within the configured input root.");
            await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var upload = await client.UploadInputAsync(content, filename ?? Path.GetFileName(path), ComfyClient.MimeFor(filename ?? path), subfolder, overwrite, cancellationToken);
            return new { success = true, name = upload.Name, subfolder = upload.Subfolder, type = upload.Type, workflow_value = upload.WorkflowValue };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { success = false, error = ex.Message }; }
    }

    [McpServerTool(Name = "comfy_job_cancel"), Description("Best-effort cancellation of one pending or currently running ComfyUI prompt.")]
    public async Task<object> ComfyJobCancel(string prompt_id, CancellationToken cancellationToken = default)
    {
        try { return await client.CancelAsync(prompt_id, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { success = false, error = ex.Message }; }
    }

    [McpServerTool(Name = "comfy_queue_list"), Description("List running and pending jobs directly from ComfyUI.")]
    public async Task<object> ComfyQueueList(CancellationToken cancellationToken = default)
    {
        try
        {
            var queue = await client.GetQueueAsync(cancellationToken);
            var running = queue["queue_running"] as JsonArray ?? [];
            var pending = queue["queue_pending"] as JsonArray ?? [];
            return new { running, pending, running_count = running.Count, pending_count = pending.Count };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { error = ex.Message }; }
    }

    [McpServerTool(Name = "comfy_queue_clear"), Description("Clear pending ComfyUI jobs without interrupting the running job.")]
    public async Task<object> ComfyQueueClear(CancellationToken cancellationToken = default)
    {
        try { await client.ClearPendingAsync(cancellationToken); return new { success = true, cleared = "pending" }; }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { success = false, error = ex.Message }; }
    }

    private async Task<CallToolResult> Run(string workflowId, JsonObject values, string? requestedMediaType, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = generationOptions.Resolve(workflowId, values);
            var mediaType = requestedMediaType ?? resolved.MediaType
                ?? throw new InvalidOperationException($"Workflow '{workflowId}' has no media_type. Run setup to configure image, audio, or video.");
            if (resolved.MediaType is not null && !string.Equals(resolved.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Workflow '{workflowId}' is configured for {resolved.MediaType}, not {mediaType}.");
            var rendered = workflows.RenderResolved(workflowId, resolved.Values);
            var completed = await execution.RunAsync(rendered, mediaType, cancellationToken);
            return MediaResult(completed, workflowId);
        }
        catch (GenerationParameterException ex)
        {
            return Error(ex.Message, new JsonObject { ["parameter"] = ex.Parameter, ["allowed_values"] = new JsonArray(ex.AllowedValues.Select(value => JsonValue.Create(value)).ToArray()) });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return Error(ex.Message); }
    }

    private Task<CallToolResult> RunGenerated(
        string mediaType,
        string? workflowId,
        JsonObject values,
        bool waitForCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedWorkflowId = ResolveWorkflowId(mediaType, workflowId);
            return waitForCompletion
                ? Run(resolvedWorkflowId, values, mediaType, cancellationToken)
                : Submit(resolvedWorkflowId, values, mediaType, cancellationToken);
        }
        catch (Exception ex) { return Task.FromResult(Error(ex.Message)); }
    }

    private async Task<CallToolResult> Submit(
        string workflowId,
        JsonObject values,
        string requestedMediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = generationOptions.Resolve(workflowId, values);
            if (resolved.MediaType is not null &&
                !string.Equals(
                    resolved.MediaType,
                    requestedMediaType,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Workflow '{workflowId}' is configured for {resolved.MediaType}, not {requestedMediaType}.");
            var rendered = workflows.RenderResolved(workflowId, resolved.Values);
            var submitted = await execution.SubmitAsync(rendered, cancellationToken);
            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    submitted = true,
                    prompt_id = submitted.PromptId,
                    queue_number = submitted.QueueNumber,
                    workflow_id = workflowId
                }),
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Submitted workflow '{workflowId}' as prompt {submitted.PromptId} without waiting for output."
                    }
                ]
            };
        }
        catch (GenerationParameterException ex)
        {
            return Error(
                ex.Message,
                new JsonObject
                {
                    ["parameter"] = ex.Parameter,
                    ["allowed_values"] = new JsonArray(
                        ex.AllowedValues.Select(value => JsonValue.Create(value)).ToArray())
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(ex.Message);
        }
    }

    private string ResolveWorkflowId(string mediaType, string? workflowId) =>
        !string.IsNullOrWhiteSpace(workflowId) ? workflowId :
        workflowConfiguration.DefaultWorkflow(mediaType) ??
        throw new InvalidOperationException($"No default {mediaType} workflow is configured. Run setup with operation set_default_workflow, or supply workflow_id.");

    private static CallToolResult MediaResult(CompletedWorkflow completed, string workflowId)
    {
        ContentBlock media = completed.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? ImageContentBlock.FromBytes(completed.Bytes, completed.MimeType)
            : completed.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                ? AudioContentBlock.FromBytes(completed.Bytes, completed.MimeType)
                : new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(completed.Bytes, $"comfyui://output/{Uri.EscapeDataString(completed.Output.Filename)}", completed.MimeType)
                };
        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                prompt_id = completed.PromptId,
                workflow_id = workflowId,
                filename = completed.Output.Filename,
                mime_type = completed.MimeType
            }),
            Content = [media, new TextContentBlock { Text = $"Completed workflow '{workflowId}' as prompt {completed.PromptId}; returning {completed.Output.Filename}." }]
        };
    }

    private static CallToolResult Error(string message, JsonObject? metadata = null) => new()
    {
        IsError = true,
        StructuredContent = metadata is null ? null : JsonSerializer.SerializeToElement(metadata),
        Content = [new TextContentBlock { Text = message }]
    };

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} is required.");

    private string ConfigureWorkflow(string workflowId, string? mediaType, JsonObject? defaults, JsonObject? allowedValues, JsonObject? constraints)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        var parameters = workflows.ExtractParameters(workflow);
        foreach (var configured in new[] { defaults, allowedValues, constraints }
                     .Where(value => value is not null)
                     .SelectMany(value => value!.Select(pair => pair.Key))
                     .Distinct(StringComparer.Ordinal))
            if (!parameters.ContainsKey(configured))
                throw new ArgumentException($"Workflow '{workflowId}' has no parameter '{configured}'.");
        return workflowConfiguration.SaveWorkflow(workflowId, mediaType, defaults, allowedValues, constraints);
    }
}
