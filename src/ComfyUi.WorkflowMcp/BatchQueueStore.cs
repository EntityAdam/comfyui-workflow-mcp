using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class BatchQueueEntry
{
    public required string Id { get; init; }
    public required WorkflowPlan Plan { get; init; }
    public required string BaseUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "pending";
    public string? BatchId { get; set; }
    public string? PromptId { get; set; }
    public int? QueueNumber { get; set; }
    public string? SavedPath { get; set; }
    public JsonArray Changes { get; set; } = [];
    public JsonObject? Error { get; set; }
}

public sealed class BatchQueueStore(IConfiguration configuration)
{
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath => Path.Combine(configuration.Directory(), "batch");
    public string FilePath => Path.Combine(DirectoryPath, "queue.json");

    private sealed class State
    {
        public required int Version { get; init; }
        public required List<BatchQueueEntry> Items { get; init; }
    }

    public JsonObject Enqueue(WorkflowPlan plan, string baseUrl) => Edit(items =>
    {
        var entry = new BatchQueueEntry { Id = Guid.NewGuid().ToString("N"), Plan = plan, BaseUrl = baseUrl };
        items.Add(entry);
        return new JsonObject
        {
            ["success"] = true,
            ["queued"] = true,
            ["submitted"] = false,
            ["entry_id"] = entry.Id,
            ["workflow_id"] = plan.WorkflowId,
            ["media_type"] = plan.MediaType,
            ["position"] = items.Count(item => item.Status == "pending"),
            ["next_step"] = "Accumulate more runs or check batch_count. Call batch_play only when the user explicitly asks to play the batch."
        };
    });

    public JsonObject Count() => Edit(items => new JsonObject
    {
        ["count"] = items.Count(item => item.Status == "pending")
    }, write: false);

    public JsonObject Remove(string entryId) => Edit(items =>
    {
        var entry = Find(items, entryId);
        if (entry.Status is not ("pending" or "failed"))
            throw new ToolFailureException("batch_entry_locked", $"Entry '{entryId}' is {entry.Status} and cannot be removed from the held queue.",
                "Use the submission receipt and ComfyUI job tools for submitted work. Removing held runs does not cancel ComfyUI jobs.");
        items.Remove(entry);
        return new JsonObject { ["success"] = true, ["entry_id"] = entryId, ["removed"] = true };
    });

    public JsonObject Clear() => Edit(items => new JsonObject
    {
        ["success"] = true,
        ["removed_count"] = items.RemoveAll(item => item.Status is "pending" or "failed")
    });

    internal FileStream AcquirePlayer()
    {
        Directory.CreateDirectory(DirectoryPath);
        try { return new FileStream(Path.Combine(DirectoryPath, "play.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw new ToolFailureException("batch_playing", "Another batch is already playing for this directory.",
                "Wait for the active batch_play call to finish before playing another batch. New runs remain held.");
        }
    }

    internal string[] Schedule(string batchId) => Edit(items =>
    {
        // A previous process may have stopped after dispatching a request. Never replay that request.
        foreach (var entry in items)
        {
            if (entry.Status == "scheduled") { entry.Status = "pending"; entry.BatchId = null; }
            if (entry.Status is "submitting" or "running")
            {
                entry.Status = entry.PromptId is null ? "uncertain" : "submitted";
                entry.Error = ToolErrors.Describe(new ToolFailureException("batch_interrupted",
                    "Previous playback ended before its final state was saved.",
                    "Inspect the ComfyUI queue or history before creating a replacement run; this entry will not replay automatically."), "Inspect the queue.");
            }
        }
        var selected = items.Where(item => item.Status == "pending").ToArray();
        foreach (var entry in selected) { entry.Status = "scheduled"; entry.BatchId = batchId; entry.Error = null; }
        return selected.Select(entry => entry.Id).ToArray();
    });

    internal BatchQueueEntry Get(string id) => Edit(items => Find(items, id), write: false);

    internal JsonObject Update(string id, Action<BatchQueueEntry> change) => Edit(items =>
    {
        var entry = Find(items, id);
        change(entry);
        return Summary(entry);
    });

    internal void ReleaseScheduled(string batchId) => Edit(items =>
    {
        foreach (var entry in items.Where(item => item.BatchId == batchId && item.Status == "scheduled"))
        {
            entry.Status = "pending";
            entry.BatchId = null;
        }
        return true;
    });

    private static BatchQueueEntry Find(List<BatchQueueEntry> items, string id) =>
        items.Find(item => item.Id == id) ?? throw new ToolFailureException("batch_entry_not_found",
            $"Batch entry '{id}' was not found.", "Use an entry_id from a generation or batch_play receipt.");

    private static JsonObject Summary(BatchQueueEntry entry) => new()
    {
        ["entry_id"] = entry.Id,
        ["status"] = entry.Status,
        ["workflow_id"] = entry.Plan.WorkflowId,
        ["media_type"] = entry.Plan.MediaType,
        ["created_at"] = entry.CreatedAt.ToString("O"),
        ["wait_for_completion"] = entry.Plan.WaitForCompletion,
        ["options"] = entry.Plan.Values.DeepClone(),
        ["batch_id"] = entry.BatchId,
        ["prompt_id"] = entry.PromptId,
        ["queue_number"] = entry.QueueNumber,
        ["saved_path"] = entry.SavedPath,
        ["output_filename"] = entry.SavedPath is null ? null : Path.GetFileName(entry.SavedPath),
        ["comfyui_url"] = entry.BaseUrl,
        ["transforms_applied"] = entry.Changes.DeepClone(),
        ["error"] = entry.Error?.DeepClone()
    };

    private T Edit<T>(Func<List<BatchQueueEntry>, T> action, bool write = true)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            FileStream storageLock;
            try { storageLock = new FileStream(Path.Combine(DirectoryPath, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                throw new ToolFailureException("batch_busy", "Another process is updating the held queue.", "Retry this queue operation; no run was submitted by this operation.");
            }
            using (storageLock)
            {
                State? state;
                try
                {
                    state = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), JsonOptions)
                        : new State { Version = 1, Items = [] };
                }
                catch (JsonException) { throw InvalidQueue(); }
                if (state is null || state.Version != 1 || state.Items is null ||
                    state.Items.Any(item => item is null || item.Plan is null || string.IsNullOrWhiteSpace(item.Id) ||
                        item.Plan.Workflow is null || item.Plan.Values is null || item.Plan.Transforms is null ||
                        item.Plan.MediaType is not ("image" or "video" or "audio") ||
                        item.Status is not ("pending" or "scheduled" or "submitting" or "running" or "submitted" or "completed" or "uncertain" or "failed" or "submitted_with_errors")) ||
                    state.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Items.Count)
                    throw InvalidQueue();
                var result = action(state.Items);
                if (write)
                {
                    var temporary = Path.Combine(DirectoryPath, $"queue.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            JsonSerializer.Serialize(file, state, JsonOptions);
                            file.Flush(flushToDisk: true);
                        }
                        File.Move(temporary, FilePath, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return result;
            }
        }
    }

    private static ToolFailureException InvalidQueue() => new("invalid_batch_queue", "The saved batch queue has an invalid or unsupported format.",
        "Restore batch/queue.json from a valid copy. Do not discard uncertain submission records without checking ComfyUI.");
}
