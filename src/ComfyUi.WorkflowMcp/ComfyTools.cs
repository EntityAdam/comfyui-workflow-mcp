using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ComfyUi.WorkflowMcp;

[McpServerToolType]
public sealed class ComfyTools(
    ComfyClient client,
    WorkflowManager workflows,
    WorkflowConfigurationManager workflowConfiguration,
    GenerationOptionsService generationOptions,
    WorkflowExecutionService execution,
    GeneratedMediaStore generatedMedia,
    MediaAssetStore mediaAssets)
{
    [McpServerTool(Name = "setup"), Description("Inspect and configure API-format ComfyUI workflows. Start with status for parsing and parameterization guidance. A workflow JSON is an object keyed by node ID; each node has class_type and inputs. Look in inputs for editable literal values such as cfg, steps, width, height, prompt, and negative/negative_prompt. Arrays like [\"node-id\", 0] are connections, not literal values. Use parameterize_workflow with workflow_id and a parameters object whose keys are public parameter names and whose values contain the exact node_id, input_name, and type.")]
    public object Setup(
        [Description("Operation: status (returns paths plus a parameterization guide), configure_workflow, parameterize_workflow, or set_default_workflow.")] string operation = "status",
        [Description("Workflow JSON filename without .json. Required except for status.")] string? workflow_id = null,
        [Description("image, audio, or video.")] string? media_type = null,
        [Description("For configure_workflow: default completion behavior for this workflow. Omit to preserve its existing value; absent YAML defaults to true.")] bool? wait_for_completion = null,
        [Description("For parameterize_workflow: additive mappings keyed by desired public parameter name. Existing PARAM_* bindings remain unchanged; supply only new or targeted bindings. Each value requires node_id (exact top-level JSON key, including colons), input_name (exact key under that node's inputs), and type: string, integer, number, or boolean. Example: {\"steps\":{\"node_id\":\"3\",\"input_name\":\"steps\",\"type\":\"integer\"}}.")] JsonObject? parameters = null,
        JsonObject? defaults = null,
        JsonObject? allowed_values = null,
        [Description("Per-parameter validation including minimum, maximum, and pattern.")] JsonObject? constraints = null)
    {
        try
        {
            return operation switch
            {
                "status" => workflowConfiguration.Status(workflows),
                "configure_workflow" => new
                {
                    success = true,
                    path = ConfigureWorkflow(Required(workflow_id, nameof(workflow_id)), media_type, wait_for_completion, defaults, allowed_values, constraints)
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

    [McpServerTool(Name = "workflow_get"), Description("Return one saved API-format ComfyUI workflow JSON as structured data for inspection. workflow_id is required; use workflow_list to discover IDs.")]
    public object WorkflowGet(
        [Description("Required workflow JSON filename without .json.")] string workflow_id)
    {
        var workflow = workflows.Load(workflow_id)
            ?? throw new ArgumentException($"Workflow '{workflow_id}' not found.");
        return new { workflow_id, workflow };
    }

    [McpServerTool(Name = "workflow_get_options"), Description("Return a workflow's YAML-backed media type, defaults, allowed values, constraints, and discovered parameters.")]
    public object WorkflowGetOptions(string workflow_id) =>
        generationOptions.GetOptions(workflow_id);

    [McpServerTool(Name = "generate_image"), Description("Run an image workflow, wait for completion, and display its first image output.")]
    public Task<CallToolResult> GenerateImage(
        string prompt,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("Override completion behavior. Omit to use this workflow's YAML wait_for_completion value, which defaults to true.")] bool? wait_for_completion = null,
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
        [Description("Override completion behavior. Omit to use this workflow's YAML wait_for_completion value, which defaults to true.")] bool? wait_for_completion = null,
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
        [Description("Override completion behavior. Omit to use this workflow's YAML wait_for_completion value, which defaults to true.")] bool? wait_for_completion = null,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["prompt"] = prompt;
        return RunGenerated("video", workflow_id, values, wait_for_completion, cancellationToken);
    }

    [McpServerTool(Name = "comfy_media_upload"), Description("Upload one media asset to ComfyUI by its filename. Use asset_stage or asset_list to obtain the filename.")]
    public async Task<object> ComfyMediaUpload(
        [Description("Asset filename only, without directory components.")] string filename,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = mediaAssets.Find(filename)
                ?? throw new ArgumentException("filename does not exist in the configured assets directory.");
            await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var upload = await client.UploadInputAsync(content, filename, ComfyClient.MimeFor(filename), overwrite: overwrite, cancellationToken: cancellationToken);
            return new { success = true, name = upload.Name, subfolder = upload.Subfolder, type = upload.Type, workflow_value = upload.WorkflowValue };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { success = false, error = ex.Message }; }
    }

    [McpServerTool(Name = "asset_stage"), Description("Save base64-encoded or data-URL media supplied by an MCP client to the managed assets directory. Use the returned filename with comfy_media_upload.")]
    public async Task<object> AssetStage(
        [Description("Raw base64 data or a base64 data URL, such as data:image/png;base64,...")] string data,
        [Description("Optional original filename. A safe unique filename is created.")] string? filename = null,
        [Description("Optional MIME type. Inferred from a data URL when omitted.")] string? mime_type = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var asset = await mediaAssets.StageAsync(data, filename, mime_type, cancellationToken);
            return new { success = true, filename = asset.Filename, mime_type = asset.MimeType, bytes = asset.Bytes };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new { success = false, error = ex.Message }; }
    }

    [McpServerTool(Name = "asset_list"), Description("List media staged with asset_stage in the managed assets directory.")]
    public object AssetList()
    {
        var assets = mediaAssets.ListAssets();
        return new
        {
            assets = assets.Select(asset => new { filename = asset.Filename, mime_type = asset.MimeType, bytes = asset.Bytes }),
            count = assets.Count
        };
    }

    [McpServerTool(Name = "asset_exists"), Description("Check whether a filename exists in the managed assets directory.")]
    public object AssetExists(
        [Description("A filename only, without directory components.")] string filename)
    {
        try
        {
            var path = mediaAssets.Find(filename);
            return new { exists = path is not null, filename };
        }
        catch (Exception ex) { return new { exists = false, filename, error = ex.Message }; }
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

    [McpServerTool(Name = "comfy_diffusion_model_list"), Description("List diffusion models installed and available to ComfyUI.")]
    public async Task<object> ComfyDiffusionModelList(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await client.GetDiffusionModelsAsync(cancellationToken);
            return new { models, count = models.Count };
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
            var savedPath = await generatedMedia.SaveAsync(completed, cancellationToken);
            return MediaResult(completed, workflowId, savedPath);
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
        bool? waitForCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedWorkflowId = ResolveWorkflowId(mediaType, workflowId);
            var shouldWait = waitForCompletion
                ?? workflowConfiguration.Load(resolvedWorkflowId).WaitForCompletion
                ?? true;
            return shouldWait
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

    private static CallToolResult MediaResult(
        CompletedWorkflow completed,
        string workflowId,
        string? savedPath)
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
                mime_type = completed.MimeType,
                saved_path = savedPath,
                output_filename = savedPath is null ? null : Path.GetFileName(savedPath)
            }),
            Content =
            [
                media,
                new TextContentBlock
                {
                    Text = savedPath is null
                        ? $"Completed workflow '{workflowId}' as prompt {completed.PromptId}; returning {completed.Output.Filename}."
                        : $"Completed workflow '{workflowId}' as prompt {completed.PromptId}; returning {completed.Output.Filename} and saved a copy to {savedPath}."
                }
            ]
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

    private string ConfigureWorkflow(string workflowId, string? mediaType, bool? waitForCompletion, JsonObject? defaults, JsonObject? allowedValues, JsonObject? constraints)
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
        return workflowConfiguration.SaveWorkflow(workflowId, mediaType, waitForCompletion, defaults, allowedValues, constraints);
    }
}
