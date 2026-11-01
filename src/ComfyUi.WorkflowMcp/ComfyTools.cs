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
    MediaAssetStore mediaAssets,
    WorkflowPreparationService preparation,
    BatchQueueService batches,
    BatchQueueStore batchStore)
{
    [McpServerTool(Name = "setup"), Description("Show server paths, default workflows, and a guide to the workflow tools. Read-only.")]
    public object Setup()
    {
        try { return workflowConfiguration.Status(workflows); }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Check the configured workflow directory and retry."); }
    }

    [McpServerTool(Name = "workflow_configure"), Description("Update workflow YAML. Updates merge; send only changes. Omitted values stay unchanged. Null entries remove configuration.")]
    public object WorkflowConfigure(
        string workflow_id,
        [Description("image, audio, or video.")] string? media_type = null,
        [Description("Default completion behavior; omitted preserves the current value.")] bool? wait_for_completion = null,
        [Description("Defaults by parameter name. Null removes a default, keeping its binding.")] JsonObject? defaults = null,
        [Description("Lists by parameter name. Each list replaces the old list; null removes it.")] JsonObject? allowed_values = null,
        [Description("Rules by parameter name: minimum, maximum, pattern, required. Merge by rule; null removes a rule or parameter's constraints.")] JsonObject? constraints = null,
        [Description("Workflow description. Empty string removes it; omitted preserves it.")] string? description = null,
        [Description("Ordered definitions with unique id, kind, and optional enabled boolean. Kinds: replace_negative_rgthree_seed, minimax_references. minimax_references requires node_id. Replaces the entire list; [] removes all transforms.")] JsonArray? transforms = null)
    {
        try
        {
            var before = workflowConfiguration.Load(workflow_id);
            ConfigureWorkflow(workflow_id, media_type, wait_for_completion, defaults, allowed_values, constraints, description, transforms);
            return ConfigurationReceipt(workflow_id, before, workflowConfiguration.Load(workflow_id));
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Read workflow_get_options and correct the configuration."); }
    }

    [McpServerTool(Name = "workflow_parameterize"), Description("Add or update workflow parameter bindings. Send only changed mappings; other bindings stay unchanged. Call setup for an example.")]
    public object WorkflowParameterize(
        string workflow_id,
        [Description("Bindings keyed by public parameter name.")] Dictionary<string, WorkflowParameterMapping> parameters)
    {
        try
        {
            var before = workflows.Load(workflow_id)
                ?? throw ToolFailureException.WorkflowNotFound(workflow_id);
            foreach (var (name, mapping) in parameters)
                if (mapping is null || string.IsNullOrWhiteSpace(mapping.NodeId) || string.IsNullOrWhiteSpace(mapping.InputName))
                    throw new ToolFailureException("invalid_binding", "node_id and input_name must be nonempty strings.", "Read workflow_get and use an existing node_id and input_name.", name);
            workflowConfiguration.ParameterizeWorkflow(workflow_id, JsonSerializer.SerializeToNode(parameters)!.AsObject(), workflows);
            var after = workflows.Load(workflow_id)!;
            var afterParameters = workflows.ExtractParameters(after);
            var changed = parameters.Where(pair =>
            {
                var node = pair.Value.NodeId;
                var input = pair.Value.InputName;
                return !JsonNode.DeepEquals(before[node]!["inputs"]![input], after[node]!["inputs"]![input]);
            }).Select(pair => afterParameters.Values.Single(parameter => parameter.Bindings.Any(binding =>
                binding.NodeId == pair.Value.NodeId && binding.InputName == pair.Value.InputName)).Name);
            var receipt = Receipt(workflow_id);
            AddNames(receipt, "parameters_updated", changed);
            AddNames(receipt, "parameters_removed", workflows.ExtractParameters(before).Keys.Except(workflows.ExtractParameters(after).Keys));
            return receipt;
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Read workflow_get and correct the parameter mapping."); }
    }

    [McpServerTool(Name = "workflow_unparameterize"), Description("Restore selected parameters as fixed inputs using their current YAML defaults, then remove their configuration. Valid defaults are required; validation errors leave files unchanged.")]
    public object WorkflowUnparameterize(
        string workflow_id,
        [Description("Parameter names to restore, for example [\"cfg\"]. Applies to every binding.")] string[] parameter_names)
    {
        try
        {
            var before = workflowConfiguration.Load(workflow_id);
            var original = workflows.Load(workflow_id)
                ?? throw ToolFailureException.WorkflowNotFound(workflow_id);
            workflowConfiguration.UnparameterizeWorkflow(workflow_id, parameter_names, workflows);
            var receipt = ConfigurationReceipt(workflow_id, before, workflowConfiguration.Load(workflow_id));
            AddNames(receipt, "parameters_restored", workflows.ExtractParameters(original).Keys.Except(workflows.ExtractParameters(workflows.Load(workflow_id)!).Keys));
            return receipt;
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Read workflow_get_options and set valid defaults before retrying."); }
    }

    [McpServerTool(Name = "workflow_set_default"), Description("Select the default workflow for image, audio, or video generation.")]
    public object WorkflowSetDefault(string workflow_id, [Description("image, audio, or video.")] string media_type)
    {
        try
        {
            var before = workflowConfiguration.DefaultWorkflow(media_type);
            workflowConfiguration.SetDefaultWorkflow(media_type, workflow_id, workflows);
            return new { success = true, workflow_id, media_type = media_type.Trim().ToLowerInvariant(), default_updated = before != workflow_id };
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Call workflow_list and choose a workflow for the requested media type."); }
    }

    [McpServerTool(Name = "workflow_list"), Description("List workflow IDs, descriptions, and media types. Use workflow_get_options for parameters, defaults, and validation.")]
    public object WorkflowList()
    {
        try
        {
            var catalog = workflows.Catalog()
                .Select(item => new { workflow_id = item.Id, description = item.Description, media_type = workflowConfiguration.Load(item.Id).MediaType })
                .ToArray();
            return new { workflows = catalog, count = catalog.Length };
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Check the configured workflow directory and retry."); }
    }

    [McpServerTool(Name = "workflow_get"), Description("Read one workflow's API-format JSON. Use workflow_list for IDs.")]
    public object WorkflowGet(
        [Description("Required workflow JSON filename without .json.")] string workflow_id)
    {
        try
        {
            var workflow = workflows.Load(workflow_id)
                ?? throw ToolFailureException.WorkflowNotFound(workflow_id);
            return new { workflow_id, workflow };
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Call workflow_list and choose an existing workflow_id."); }
    }

    [McpServerTool(Name = "workflow_get_options"), Description("Read one workflow's parameters, defaults, validation rules, and metadata.")]
    public object WorkflowGetOptions(string workflow_id)
    {
        try { return generationOptions.GetOptions(workflow_id); }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Call workflow_list and choose an existing workflow_id."); }
    }

    [McpServerTool(Name = "workflow_preview"), Description("Prepare a workflow without submitting it. Returns the rendered graph and transform changes. Enabled transforms inspect installed ComfyUI node schemas. Use the same options shape as generation, including prompt.")]
    public async Task<object> WorkflowPreview(string workflow_id, JsonObject? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var prepared = await preparation.PrepareAsync(workflow_id, options ?? [], cancellationToken);
            return new { workflow_id, workflow = prepared.Workflow, transforms_applied = prepared.Changes };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolErrors.Describe(ex, "Read workflow_get_options and correct the workflow or options.");
        }
    }

    [McpServerTool(Name = "generate_image"), Description("Add an image run to the held batch by default; no ComfyUI job starts. Play only when the user explicitly asks, using batch_play. enqueue=false is for an explicitly requested immediate run. Waiting returns the first image; wait_for_completion=false returns a prompt ID when played or run immediately.")]
    public Task<CallToolResult> GenerateImage(
        string prompt,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("True waits for media; false returns a prompt ID. Omitted uses YAML, defaulting to true.")] bool? wait_for_completion = null,
        [Description("Default true holds this run until the user asks to play the batch. Set false only for an explicitly requested immediate run.")] bool enqueue = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["prompt"] = prompt;
        return RunGenerated("image", workflow_id, values, wait_for_completion, enqueue, cancellationToken);
    }

    [McpServerTool(Name = "generate_audio"), Description("Add an audio run to the held batch by default; no ComfyUI job starts. Use batch_play only when the user asks to play. enqueue=false explicitly runs immediately. Waiting returns the first audio; wait_for_completion=false returns a prompt ID when played or run immediately.")]
    public Task<CallToolResult> GenerateAudio(
        string tags,
        string lyrics,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("True waits for media; false returns a prompt ID. Omitted uses YAML, defaulting to true.")] bool? wait_for_completion = null,
        [Description("Default true holds this run until the user asks to play the batch. Set false only for an explicitly requested immediate run.")] bool enqueue = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["tags"] = tags;
        values["lyrics"] = lyrics;
        return RunGenerated("audio", workflow_id, values, wait_for_completion, enqueue, cancellationToken);
    }

    [McpServerTool(Name = "generate_video"), Description("Add a video run to the held batch by default; no ComfyUI job starts. Use batch_play only when the user asks to play. enqueue=false explicitly runs immediately. Waiting returns the first video; wait_for_completion=false returns a prompt ID when played or run immediately.")]
    public Task<CallToolResult> GenerateVideo(
        string prompt,
        string? workflow_id = null,
        JsonObject? options = null,
        [Description("True waits for media; false returns a prompt ID. Omitted uses YAML, defaulting to true.")] bool? wait_for_completion = null,
        [Description("Default true holds this run until the user asks to play the batch. Set false only for an explicitly requested immediate run.")] bool enqueue = true,
        CancellationToken cancellationToken = default)
    {
        var values = options?.DeepClone().AsObject() ?? [];
        values["prompt"] = prompt;
        return RunGenerated("video", workflow_id, values, wait_for_completion, enqueue, cancellationToken);
    }

    [McpServerTool(Name = "batch_count"), Description("Return only the count of pending held runs awaiting Play. Excludes runs already selected for playback, failed runs, and submission history. Read-only; never starts ComfyUI jobs.")]
    public object BatchCount()
    {
        try { return batchStore.Count(); }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Inspect the saved batch queue and retry."); }
    }

    [McpServerTool(Name = "batch_remove"), Description("Remove one pending or failed held run by entry_id. Does not cancel submitted ComfyUI jobs.")]
    public object BatchRemove(string entry_id)
    {
        try { return batchStore.Remove(entry_id); }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Use an entry_id from a generation or batch_play receipt for a pending or failed run."); }
    }

    [McpServerTool(Name = "batch_clear"), Description("Remove pending and failed held runs. Does not stop active playback or cancel ComfyUI jobs; submission history is retained.")]
    public object BatchClear()
    {
        try { return batchStore.Clear(); }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Inspect batch_count and retry."); }
    }

    [McpServerTool(Name = "batch_play"), Description("Play the currently pending batch in order. Call ONLY when the user explicitly asks to play/start the batch; never automatically after generation calls. New additions remain held for a later Play. Stops on the first error and never replays submitted or uncertain runs.")]
    public async Task<CallToolResult> BatchPlay(
        [Description("Omitted honors each queued run's completion setting. False submits the batch without waiting for outputs; true waits and returns media.")] bool? wait_for_completion = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var playback = await batches.PlayAsync(wait_for_completion, cancellationToken);
            var content = new List<ContentBlock>();
            foreach (var item in playback.Media)
            {
                content.Add(new TextContentBlock { Text = $"Batch entry {item.EntryId}:" });
                content.AddRange(MediaResult(item.Completed, item.WorkflowId, item.SavedPath, item.Changes).Content);
            }
            content.Add(new TextContentBlock { Text = playback.Receipt.ToJsonString() });
            return new CallToolResult
            {
                IsError = playback.Failed,
                StructuredContent = JsonSerializer.SerializeToElement(playback.Receipt),
                Content = content
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return Error(ex); }
    }

    [McpServerTool(Name = "comfy_media_upload"), Description("Upload an existing asset to ComfyUI by filename. Use asset_list to find it; no base64 is needed.")]
    public async Task<object> ComfyMediaUpload(
        [Description("Asset filename only, without directory components.")] string filename,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = mediaAssets.Find(filename)
                ?? throw new ToolFailureException("asset_not_found", "filename does not exist in the configured assets directory.", "Call asset_list and choose an existing filename.", nameof(filename));
            await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var upload = await client.UploadInputAsync(content, filename, ComfyClient.MimeFor(filename), overwrite: overwrite, cancellationToken: cancellationToken);
            return new { success = true, name = upload.Name, subfolder = upload.Subfolder, type = upload.Type, workflow_value = upload.WorkflowValue };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Call asset_list and use an existing asset filename."); }
    }

    [McpServerTool(Name = "asset_stage"), Description("Stage media bytes supplied by the client. Never generate or reproduce base64 yourself. For existing assets, use asset_list and a filename instead.")]
    public async Task<object> AssetStage(
        [Description("Base64 or data URL supplied directly by the client; do not synthesize or transcribe media bytes.")] string data,
        [Description("Optional original filename. A safe unique filename is created.")] string? filename = null,
        [Description("Optional MIME type. Inferred from a data URL when omitted.")] string? mime_type = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var asset = await mediaAssets.StageAsync(data, filename, mime_type, cancellationToken);
            return new { success = true, filename = asset.Filename, mime_type = asset.MimeType, bytes = asset.Bytes };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Have the client supply valid media bytes directly through asset_stage."); }
    }

    [McpServerTool(Name = "asset_list"), Description("List recognized image, video, and audio files in the managed assets directory. Other file types are omitted; media types are inferred from filename extensions.")]
    public object AssetList()
    {
        try
        {
            var assets = mediaAssets.ListAssets();
            return new
            {
                assets = assets.Select(asset => new { filename = asset.Filename, mime_type = asset.MimeType, bytes = asset.Bytes }),
                count = assets.Count
            };
        }
        catch (Exception ex) { return ToolErrors.Describe(ex, "Check the configured assets directory and retry."); }
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
        catch (Exception ex) { return ToolErrors.Describe(ex, "Call asset_list and use a filename without directory components."); }
    }

    [McpServerTool(Name = "asset_view"), Description("Return an existing image as MCP image content. Use asset_list for filenames. Visual inspection requires a vision model and a client that forwards tool images.")]
    public async Task<CallToolResult> AssetView(
        [Description("Image asset filename only, without directory components.")] string filename,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await mediaAssets.ReadImageAsync(filename, cancellationToken);
            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    filename,
                    mime_type = image.MimeType,
                    bytes = image.Bytes.Length
                }),
                Content =
                [
                    ImageContentBlock.FromBytes(image.Bytes, image.MimeType),
                    new TextContentBlock
                    {
                        Text = $"Returned image asset '{filename}'. Visual inspection requires the client to forward this image to a vision-capable model."
                    }
                ]
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Result(ex, "Call asset_list and choose an existing image filename."); }
    }

    [McpServerTool(Name = "comfy_job_cancel"), Description("Best-effort cancellation of one pending or currently running ComfyUI prompt.")]
    public async Task<object> ComfyJobCancel(string prompt_id, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await client.CancelAsync(prompt_id, cancellationToken);
            if (result["success"]?.GetValue<bool>() == false)
                throw new ToolFailureException("job_not_found", $"Prompt '{prompt_id}' is not running or pending.", "Call comfy_queue_list and choose a running or pending prompt_id.", nameof(prompt_id));
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Check the tool arguments and retry."); }
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
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Check the ComfyUI connection and retry."); }
    }

    [McpServerTool(Name = "comfy_diffusion_model_list"), Description("List diffusion models installed and available to ComfyUI.")]
    public async Task<object> ComfyDiffusionModelList(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await client.GetDiffusionModelsAsync(cancellationToken);
            return new { models, count = models.Count };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Check the ComfyUI connection and retry."); }
    }

    [McpServerTool(Name = "comfy_queue_clear"), Description("Clear pending ComfyUI jobs without interrupting the running job.")]
    public async Task<object> ComfyQueueClear(CancellationToken cancellationToken = default)
    {
        try { await client.ClearPendingAsync(cancellationToken); return new { success = true, cleared = "pending" }; }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ToolErrors.Describe(ex, "Check the tool arguments and retry."); }
    }

    private async Task<CallToolResult> Run(string workflowId, JsonObject values, string? requestedMediaType, CancellationToken cancellationToken)
    {
        try
        {
            var prepared = await preparation.PrepareAsync(workflowId, values, cancellationToken);
            var mediaType = requestedMediaType ?? prepared.MediaType
                ?? throw new InvalidOperationException($"Workflow '{workflowId}' has no media_type. Use workflow_configure to set image, audio, or video.");
            if (prepared.MediaType is not null && !string.Equals(prepared.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException("media_type_mismatch", $"Workflow '{workflowId}' is configured for {prepared.MediaType}, not {mediaType}.", $"Choose a workflow configured for {mediaType} generation.");
            var completed = await execution.RunAsync(prepared.Workflow, mediaType, cancellationToken);
            var savedPath = await generatedMedia.SaveAsync(completed, cancellationToken);
            return MediaResult(completed, workflowId, savedPath, prepared.Changes);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return Error(ex); }
    }

    private Task<CallToolResult> RunGenerated(
        string mediaType,
        string? workflowId,
        JsonObject values,
        bool? waitForCompletion,
        bool enqueue,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedWorkflowId = ResolveWorkflowId(mediaType, workflowId);
            if (enqueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queued = batches.Enqueue(resolvedWorkflowId, values, mediaType, waitForCompletion);
                return Task.FromResult(new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(queued),
                    Content = [new TextContentBlock { Text = queued.ToJsonString() }]
                });
            }
            var shouldWait = waitForCompletion
                ?? workflowConfiguration.Load(resolvedWorkflowId).WaitForCompletion
                ?? true;
            return shouldWait
                ? Run(resolvedWorkflowId, values, mediaType, cancellationToken)
                : Submit(resolvedWorkflowId, values, mediaType, cancellationToken);
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    private async Task<CallToolResult> Submit(
        string workflowId,
        JsonObject values,
        string requestedMediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            var prepared = await preparation.PrepareAsync(workflowId, values, cancellationToken);
            if (prepared.MediaType is not null &&
                !string.Equals(
                    prepared.MediaType,
                    requestedMediaType,
                    StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException("media_type_mismatch",
                    $"Workflow '{workflowId}' is configured for {prepared.MediaType}, not {requestedMediaType}.", $"Choose a workflow configured for {requestedMediaType} generation.");
            var submitted = await execution.SubmitAsync(prepared.Workflow, cancellationToken);
            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    submitted = true,
                    prompt_id = submitted.PromptId,
                    queue_number = submitted.QueueNumber,
                    workflow_id = workflowId,
                    transforms_applied = prepared.Changes
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
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(ex);
        }
    }

    private string ResolveWorkflowId(string mediaType, string? workflowId) =>
        !string.IsNullOrWhiteSpace(workflowId) ? workflowId :
        workflowConfiguration.DefaultWorkflow(mediaType) ??
        throw new ToolFailureException("default_workflow_not_set", $"No default {mediaType} workflow is configured.", $"Call workflow_set_default for {mediaType} generation.");

    private static CallToolResult MediaResult(
        CompletedWorkflow completed,
        string workflowId,
        string? savedPath,
        JsonArray changes)
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
                output_filename = savedPath is null ? null : Path.GetFileName(savedPath),
                transforms_applied = changes
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

    private static CallToolResult Error(Exception exception) =>
        ToolErrors.Result(exception, "Inspect this workflow in ComfyUI before retrying.");

    private static JsonObject Receipt(string workflowId) => new() { ["success"] = true, ["workflow_id"] = workflowId };

    private static void AddNames(JsonObject receipt, string field, IEnumerable<string> names)
    {
        var values = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length > 0) receipt[field] = new JsonArray(values.Select(name => JsonValue.Create(name)).ToArray());
    }

    private static JsonObject ConfigurationReceipt(string workflowId, WorkflowValidation before, WorkflowValidation after)
    {
        var receipt = Receipt(workflowId);
        AddChanges(receipt, "defaults", before.Defaults, after.Defaults);
        AddChanges(receipt, "allowed_values", before.AllowedValues, after.AllowedValues);
        AddChanges(receipt, "constraints", before.Constraints, after.Constraints);
        if (!JsonNode.DeepEquals(before.Transforms, after.Transforms)) receipt["transforms_updated"] = true;
        var oldFields = new JsonObject { ["description"] = before.Description, ["media_type"] = before.MediaType, ["wait_for_completion"] = before.WaitForCompletion };
        var newFields = new JsonObject { ["description"] = after.Description, ["media_type"] = after.MediaType, ["wait_for_completion"] = after.WaitForCompletion };
        AddNames(receipt, "fields_updated", newFields.Where(pair => pair.Value is not null && !JsonNode.DeepEquals(pair.Value, oldFields[pair.Key])).Select(pair => pair.Key));
        AddNames(receipt, "fields_removed", oldFields.Where(pair => pair.Value is not null && newFields[pair.Key] is null).Select(pair => pair.Key));
        return receipt;
    }

    private static void AddChanges(JsonObject receipt, string section, JsonObject before, JsonObject after)
    {
        AddNames(receipt, section + "_updated", after.Where(pair => !before.ContainsKey(pair.Key) || !JsonNode.DeepEquals(pair.Value, before[pair.Key])).Select(pair => pair.Key));
        AddNames(receipt, section + "_removed", before.Select(pair => pair.Key).Except(after.Select(pair => pair.Key)));
    }

    private string ConfigureWorkflow(string workflowId, string? mediaType, bool? waitForCompletion, JsonObject? defaults, JsonObject? allowedValues, JsonObject? constraints, string? description, JsonArray? transforms)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        var parameters = workflows.ExtractParameters(workflow);
        var effectiveTransforms = transforms ?? workflowConfiguration.Load(workflowId).Transforms;
        WorkflowTransforms.ValidateTargets(workflow, effectiveTransforms);
        var references = WorkflowTransforms.HasReferences(effectiveTransforms);
        foreach (var configured in new[] { defaults, allowedValues, constraints }
                     .Where(value => value is not null)
                     .SelectMany(value => value!.Where(pair => pair.Value is not null).Select(pair => pair.Key))
                     .Distinct(StringComparer.Ordinal))
            if (!parameters.ContainsKey(configured) && !(references && WorkflowTransforms.MediaParameters.Contains(configured, StringComparer.Ordinal)))
                throw ToolFailureException.UnknownParameter(workflowId, configured);
        if (references && defaults is not null)
            foreach (var name in WorkflowTransforms.MediaParameters)
                if (defaults[name] is { } value) WorkflowTransforms.ValidateMedia(name, value);
        return workflowConfiguration.SaveWorkflow(workflowId, mediaType, waitForCompletion, defaults, allowedValues, constraints, description, transforms);
    }
}
