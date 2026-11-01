using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed record BatchCompletedMedia(string EntryId, string WorkflowId, CompletedWorkflow Completed, string SavedPath, JsonArray Changes);
public sealed record BatchPlayback(JsonObject Receipt, IReadOnlyList<BatchCompletedMedia> Media, bool Failed);

public sealed class BatchQueueService(
    BatchQueueStore store,
    WorkflowPreparationService preparation,
    WorkflowExecutionService execution,
    GeneratedMediaStore generatedMedia,
    ComfyClient client)
{
    public JsonObject Enqueue(string workflowId, JsonObject values, string mediaType, bool? waitForCompletion)
    {
        var plan = preparation.Capture(workflowId, values);
        if (plan.MediaType is not null && !string.Equals(plan.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            throw new ToolFailureException("media_type_mismatch", $"Workflow '{workflowId}' is configured for {plan.MediaType}, not {mediaType}.",
                $"Choose a workflow configured for {mediaType} generation.");
        return store.Enqueue(plan with { MediaType = mediaType, WaitForCompletion = waitForCompletion ?? plan.WaitForCompletion }, client.BaseUrl);
    }

    public async Task<BatchPlayback> PlayAsync(bool? waitForCompletion = null, CancellationToken cancellationToken = default)
    {
        using var player = store.AcquirePlayer();
        var batchId = Guid.NewGuid().ToString("N");
        var selected = store.Schedule(batchId);
        var results = new JsonArray();
        var media = new List<BatchCompletedMedia>();
        var failed = false;
        try
        {
            foreach (var id in selected)
            {
                var started = false;
                string? promptId = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = store.Get(id);
                    if (entry.BaseUrl != client.BaseUrl)
                        throw new ToolFailureException("batch_server_changed", "This run was queued for a different ComfyUI URL.",
                            "Restore the original ComfyUI URL, or remove this held run and enqueue it for the current server.");
                    var prepared = await preparation.PrepareAsync(entry.Plan, cancellationToken);
                    var wait = waitForCompletion ?? entry.Plan.WaitForCompletion;
                    Task Submitting()
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        store.Update(id, item => { item.Status = "submitting"; item.Changes = prepared.Changes; });
                        started = true;
                        return Task.CompletedTask;
                    }
                    Task Submitted(ComfySubmissionResult submission)
                    {
                        promptId = submission.PromptId;
                        store.Update(id, item =>
                        {
                            item.PromptId = submission.PromptId;
                            item.QueueNumber = submission.QueueNumber;
                            item.Status = wait ? "running" : "submitted";
                        });
                        return Task.CompletedTask;
                    }
                    if (wait)
                    {
                        var completed = await execution.RunAsync(prepared.Workflow, entry.Plan.MediaType!, cancellationToken, Submitted, Submitting);
                        var path = await generatedMedia.SaveAsync(completed, cancellationToken);
                        results.Add(store.Update(id, item => { item.Status = "completed"; item.SavedPath = path; }));
                        media.Add(new BatchCompletedMedia(id, entry.Plan.WorkflowId, completed, path, prepared.Changes));
                    }
                    else
                    {
                        await Submitting();
                        var submission = await execution.SubmitAsync(prepared.Workflow, cancellationToken);
                        await Submitted(submission);
                        results.Add(store.Update(id, _ => { }));
                    }
                }
                catch (Exception ex)
                {
                    var rejection = (ex as ComfyWorkflowRejectedException)?.Details();
                    promptId ??= rejection?["prompt_id"]?.ToString();
                    var ambiguous = started && (rejection is null || rejection["may_have_queued"]?.GetValue<bool>() == true);
                    var error = cancellationToken.IsCancellationRequested
                        ? ToolErrors.Describe(new ToolFailureException("batch_interrupted", "Batch playback was interrupted.",
                            "Inspect saved batch/queue.json and the ComfyUI queue before replacing any run with an uncertain submission."), "Inspect saved batch/queue.json.")
                        : ToolErrors.Describe(ex, "Use this error and the entry_id from its generation receipt to correct the failed run before enqueuing a replacement.");
                    results.Add(store.Update(id, item =>
                    {
                        item.PromptId = promptId;
                        item.Status = promptId is not null ? "submitted_with_errors" : ambiguous ? "uncertain" : "failed";
                        if (cancellationToken.IsCancellationRequested && !started) item.Status = "pending";
                        item.Error = error;
                    }));
                    if (cancellationToken.IsCancellationRequested) throw;
                    failed = true;
                    break;
                }
            }
        }
        finally { store.ReleaseScheduled(batchId); }
        var receipt = new JsonObject
        {
            ["success"] = !failed,
            ["batch_id"] = batchId,
            ["selected_count"] = selected.Length,
            ["processed_count"] = results.Count,
            ["stopped_on_error"] = failed,
            ["runs"] = results,
            ["next_step"] = failed
                ? "Review these run results. Unstarted runs remain pending; nothing resumes automatically. Inspect uncertain or submitted jobs before creating replacements."
                : "Check batch_count for remaining held runs or review returned media. New held runs require another explicit batch_play."
        };
        if (failed)
        {
            receipt["error"] = "batch_stopped";
            receipt["message"] = "Batch playback stopped on an error. Earlier results and unstarted pending runs are preserved.";
        }
        return new BatchPlayback(receipt, media, failed);
    }
}
