using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class McpServerIntegrationTests(ComfyWorkflowMcpApplicationFactory factory)
    : IClassFixture<ComfyWorkflowMcpApplicationFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task HealthEndpointReturnsServerConfiguration()
    {
        using var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ok", body!["status"]!.ToString());
    }

    [Fact]
    public async Task ToolsListContainsOnlyApprovedSurface()
    {
        var response = await SendMcpAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        var names = response["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.ToString()).ToArray();
        string[] expected =
        [
            "setup", "workflow_list", "generate_image", "generate_audio", "generate_video",
            "comfy_media_upload", "comfy_job_cancel", "comfy_queue_list", "comfy_queue_clear"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task GenerationToolsExposeCompletionChoiceWithoutWorkflowRunTools()
    {
        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}""");
        var tools = response["result"]!["tools"]!.AsArray();

        Assert.DoesNotContain(
            tools,
            tool => tool!["name"]!.ToString() is
                "workflow_run" or "workflow_run_on_server");
        foreach (var name in new[] { "generate_image", "generate_audio", "generate_video" })
        {
            var tool = tools.Single(item => item!["name"]!.ToString() == name)!;
            Assert.True(
                tool["inputSchema"]!["properties"]!["wait_for_completion"]!["default"]!
                    .GetValue<bool>());
        }
    }

    [Fact]
    public async Task WorkflowListIncludesValidationWithoutCallingComfyUi()
    {
        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"workflow_list","arguments":{}}}""");
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        Assert.True(structured["count"]!.GetValue<int>() > 0);
    }

    private async Task<JsonObject> SendMcpAsync(string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var data = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonNode.Parse(data)!.AsObject();
    }
}
