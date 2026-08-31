using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowExecutionTests
{
    [Fact]
    public async Task WaitModeUsesWebSocketHistoryAndReturnsFirstRequestedOutput()
    {
        await using var server = new FakeComfyUiServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        var client = new ComfyClient(http, TestConfiguration.Create(("ComfyWorkflowMcp:BaseUrl", server.BaseUrl)));
        var service = new WorkflowExecutionService(client);

        var completed = await service.RunAsync(new JsonObject { ["1"] = new JsonObject() }, "image", TestContext.Current.CancellationToken);

        Assert.Equal("prompt-1", completed.PromptId);
        Assert.Equal("image/png", completed.MimeType);
        Assert.NotEmpty(completed.Bytes);
        Assert.Contains("prompt-1", server.HistoryRequests);
        Assert.NotNull(server.LastWebSocketClientId);
    }

    [Fact]
    public async Task SubmitModeReturnsWithoutOpeningWebSocketOrReadingHistory()
    {
        await using var server = new FakeComfyUiServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        var client = new ComfyClient(http, TestConfiguration.Create(("ComfyWorkflowMcp:BaseUrl", server.BaseUrl)));
        var service = new WorkflowExecutionService(client);

        var submitted = await service.SubmitAsync(new JsonObject { ["1"] = new JsonObject() }, TestContext.Current.CancellationToken);

        Assert.Equal("prompt-1", submitted.PromptId);
        Assert.Empty(server.HistoryRequests);
        Assert.Null(server.LastWebSocketClientId);
    }
}
