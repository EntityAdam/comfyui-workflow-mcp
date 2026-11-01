using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class BatchQueueTests
{
    [Fact]
    public void EnqueueWorksOfflineAndFreezesOptionsSeedsAndTemplatesAcrossRestart()
    {
        using var fixture = new Fixture("http://127.0.0.1:1");
        var options = new JsonObject { ["prompt"] = "original" };
        var receipt = fixture.Batches.Enqueue("image", options, "image", false);
        Assert.True(receipt["queued"]!.GetValue<bool>());
        Assert.False(receipt["submitted"]!.GetValue<bool>());
        options["prompt"] = "changed";
        var id = receipt["entry_id"]!.ToString();
        var restarted = new BatchQueueStore(fixture.Configuration);
        Assert.Equal(1, restarted.Count()["count"]!.GetValue<int>());
        var entry = restarted.Get(id);
        Assert.Equal("original", entry.Plan.Values["prompt"]!.ToString());
        Assert.True(entry.Plan.Values["seed"]!.GetValue<long>() >= 0);
        Assert.Equal(entry.Plan.Values["seed"]!.ToString(), entry.Plan.Workflow["1"]!["inputs"]!["seed"]!.ToString());
        Assert.Equal("pending", entry.Status);
        Assert.False(entry.Plan.WaitForCompletion);
    }

    [Fact]
    public async Task PlaySubmitsFrozenPlansInOrderAndNeverReplaysThem()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var first = fixture.Queue("first");
        var second = fixture.Queue("second");
        File.Delete(fixture.WorkflowPath);
        var result = await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.False(result.Failed);
        Assert.Equal(2, result.Receipt["processed_count"]!.GetValue<int>());
        Assert.Equal(["first", "second"], comfy.SubmittedPrompts.Select(item => item["prompt"]!["1"]!["inputs"]!["prompt"]!.ToString()));
        Assert.Equal("submitted", fixture.Store.Get(first).Status);
        Assert.Equal("submitted", fixture.Store.Get(second).Status);
        Assert.Equal(0, fixture.Store.Count()["count"]!.GetValue<int>());
        var again = await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Receipt["selected_count"]!.GetValue<int>());
        Assert.Equal(2, comfy.SubmissionCount);
    }

    [Fact]
    public async Task WaitingPlaybackReturnsAndSavesMediaWithPromptIdRecorded()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var id = fixture.Queue("image");
        var result = await fixture.Batches.PlayAsync(true, TestContext.Current.CancellationToken);
        var media = Assert.Single(result.Media);
        Assert.True(File.Exists(media.SavedPath));
        Assert.Equal(id, media.EntryId);
        var saved = new BatchQueueStore(fixture.Configuration).Get(id);
        Assert.Equal("completed", saved.Status);
        Assert.Equal("prompt-1", saved.PromptId);
        Assert.Equal(media.SavedPath, saved.SavedPath);
    }

    [Fact]
    public async Task NewRunsStayHeldAndConcurrentPlayIsRejected()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        fixture.Queue("first");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        comfy.BeforeSubmission = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        var play = fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var later = fixture.Queue("later");
            var other = fixture.CreateService(new BatchQueueStore(fixture.Configuration));
            var error = await Assert.ThrowsAsync<ToolFailureException>(() => other.PlayAsync(false, TestContext.Current.CancellationToken));
            Assert.Equal("batch_playing", error.Code);
            Assert.Equal("pending", fixture.Store.Get(later).Status);
        }
        finally { release.TrySetResult(); }
        Assert.False((await play).Failed);
        Assert.Equal(1, comfy.SubmissionCount);
        Assert.Equal(1, fixture.Store.Count()["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExplicitRejectionStopsBatchAndLeavesRemainingPending()
    {
        await using var comfy = new FakeComfyUiServer { SubmissionError = JsonNode.Parse(ComfyClientTests.Rejection)!.AsObject() };
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var first = fixture.Queue("first");
        var second = fixture.Queue("second");
        var result = await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.True(result.Failed);
        Assert.Equal("failed", fixture.Store.Get(first).Status);
        Assert.Equal("pending", fixture.Store.Get(second).Status);
        Assert.Equal("steps", fixture.Store.Get(first).Error!["validation_errors"]![0]!["input_name"]!.ToString());
        Assert.Equal(1, comfy.SubmissionCount);
        comfy.SubmissionError = null;
        await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(2, comfy.SubmissionCount);
        Assert.Equal("failed", fixture.Store.Get(first).Status);
        Assert.Equal("submitted", fixture.Store.Get(second).Status);
    }

    [Fact]
    public async Task PartialAcceptanceKeepsPromptIdAndDoesNotReplay()
    {
        var rejection = JsonNode.Parse(ComfyClientTests.Rejection)!.AsObject();
        rejection["prompt_id"] = "partially-accepted";
        await using var comfy = new FakeComfyUiServer { SubmissionError = rejection, SubmissionStatusCode = 200 };
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var id = fixture.Queue("first");
        Assert.True((await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken)).Failed);
        var saved = fixture.Store.Get(id);
        Assert.Equal("submitted_with_errors", saved.Status);
        Assert.Equal("partially-accepted", saved.PromptId);
        await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, comfy.SubmissionCount);
    }

    [Fact]
    public async Task InterruptedDispatchBecomesUncertainWhileUnstartedRunsCanPlay()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var first = fixture.Queue("first");
        var second = fixture.Queue("second");
        fixture.Store.Schedule("interrupted-batch");
        fixture.Store.Update(first, entry => entry.Status = "submitting");
        var restarted = new BatchQueueStore(fixture.Configuration);
        await fixture.CreateService(restarted).PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("uncertain", restarted.Get(first).Status);
        Assert.Equal("submitted", restarted.Get(second).Status);
        Assert.Equal(1, comfy.SubmissionCount);
        Assert.Equal("second", comfy.LastPromptBody!["prompt"]!["1"]!["inputs"]!["prompt"]!.ToString());
    }

    [Fact]
    public void RemoveAndClearAffectHeldRunsOnlyAndCorruptionIsNotOverwritten()
    {
        using var fixture = new Fixture("http://127.0.0.1:1");
        var first = fixture.Queue("first");
        var second = fixture.Queue("second");
        var third = fixture.Queue("third");
        fixture.Store.Update(third, entry => { entry.Status = "submitted"; entry.PromptId = "existing"; });
        fixture.Store.Remove(first);
        Assert.Throws<ToolFailureException>(() => fixture.Store.Remove(third));
        Assert.Equal(1, fixture.Store.Clear()["removed_count"]!.GetValue<int>());
        Assert.Equal("submitted", fixture.Store.Get(third).Status);
        Assert.Equal(0, fixture.Store.Count()["count"]!.GetValue<int>());
        Assert.Throws<ToolFailureException>(() => fixture.Store.Get(second));
        File.WriteAllText(fixture.Store.FilePath, "broken JSON");
        Assert.Equal("invalid_batch_queue", Assert.Throws<ToolFailureException>(() => fixture.Queue("new")).Code);
        Assert.Equal("broken JSON", File.ReadAllText(fixture.Store.FilePath));
    }

    [Fact]
    public async Task CancellationAfterSubmissionNeverMakesRunEligibleForReplay()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var first = fixture.Queue("first");
        var second = fixture.Queue("second");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        comfy.BeforeSubmission = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Batches.PlayAsync(false, cancellation.Token));
        Assert.True(fixture.Store.Get(first).Status is "uncertain" or "submitted_with_errors" or "submitted");
        Assert.Equal("pending", fixture.Store.Get(second).Status);
        Assert.Equal(1, comfy.SubmissionCount);
    }

    [Fact]
    public async Task RestartPreservesAlreadyKnownPromptAndDoesNotSubmitItAgain()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        var first = fixture.Queue("first");
        fixture.Queue("second");
        fixture.Store.Schedule("interrupted-batch");
        fixture.Store.Update(first, entry => { entry.Status = "running"; entry.PromptId = "existing-prompt"; });
        var restarted = new BatchQueueStore(fixture.Configuration);
        await fixture.CreateService(restarted).PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("submitted", restarted.Get(first).Status);
        Assert.Equal("existing-prompt", restarted.Get(first).PromptId);
        Assert.Equal(1, comfy.SubmissionCount);
    }

    [Fact]
    public async Task TransformsAreInspectedAtPlayAndPreparationFailureDoesNotSubmit()
    {
        await using var comfy = new FakeComfyUiServer();
        await comfy.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new Fixture(comfy.BaseUrl);
        File.WriteAllText(fixture.WorkflowPath, WorkflowPreparationTests.ReferenceGraph);
        File.WriteAllText(Path.ChangeExtension(fixture.WorkflowPath, ".yaml"), WorkflowPreparationTests.ReferenceYaml);
        var first = fixture.Queue("first");
        fixture.Queue("second");
        Assert.Empty(comfy.NodeDefinitionRequests);
        Assert.Equal(0, comfy.SubmissionCount);
        var result = await fixture.Batches.PlayAsync(false, TestContext.Current.CancellationToken);
        Assert.True(result.Failed);
        Assert.Equal("failed", fixture.Store.Get(first).Status);
        Assert.Equal("node_unavailable", fixture.Store.Get(first).Error!["error"]!.ToString());
        Assert.Equal(0, comfy.SubmissionCount);
        Assert.Equal(1, fixture.Store.Count()["count"]!.GetValue<int>());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly HttpClient _http = new();
        private readonly WorkflowPreparationService _preparation;
        private readonly WorkflowExecutionService _execution;
        private readonly GeneratedMediaStore _media;
        private readonly ComfyClient _client;
        internal Microsoft.Extensions.Configuration.IConfiguration Configuration { get; }
        internal string WorkflowPath { get; }
        internal BatchQueueStore Store { get; }
        internal BatchQueueService Batches { get; }

        internal Fixture(string baseUrl)
        {
            var directory = Path.Combine(_temp.Path, "workflows");
            Directory.CreateDirectory(directory);
            WorkflowPath = Path.Combine(directory, "image.json");
            File.WriteAllText(WorkflowPath, """{"1":{"class_type":"Inputs","inputs":{"prompt":"PARAM_PROMPT","seed":"PARAM_INT_SEED"}}}""");
            File.WriteAllText(Path.ChangeExtension(WorkflowPath, ".yaml"), "media_type: image\n");
            Configuration = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", _temp.Path), ("ComfyWorkflowMcp:BaseUrl", baseUrl));
            var configuration = new WorkflowConfigurationManager(Configuration);
            var workflows = new WorkflowManager(Configuration, configuration);
            _client = new ComfyClient(_http, Configuration);
            _preparation = new WorkflowPreparationService(workflows, configuration, new GenerationOptionsService(workflows, configuration), _client);
            _execution = new WorkflowExecutionService(_client);
            _media = new GeneratedMediaStore(Configuration);
            Store = new BatchQueueStore(Configuration);
            Batches = CreateService(Store);
        }

        internal BatchQueueService CreateService(BatchQueueStore store) => new(store, _preparation, _execution, _media, _client);
        internal string Queue(string prompt) => Batches.Enqueue("image", new JsonObject { ["prompt"] = prompt }, "image", null)["entry_id"]!.ToString();
        public void Dispose() { _http.Dispose(); _temp.Dispose(); }
    }
}
