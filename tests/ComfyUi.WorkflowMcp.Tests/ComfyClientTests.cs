using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class ComfyClientTests
{
    [Fact]
    public async Task MediaUploadUsesComfyUiUploadEndpointForVideo()
    {
        string? path = null;
        string? filename = null;
        string? contentType = null;
        var handler = new AsyncHandler(async request =>
        {
            path = request.RequestUri!.AbsolutePath;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = form.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image");
            filename = file.Headers.ContentDisposition!.FileName!.Trim('"');
            contentType = file.Headers.ContentType!.MediaType;
            await file.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            return Json("""{"name":"clip.mp4","subfolder":"refs","type":"input"}""");
        });
        using var http = new HttpClient(handler);
        var client = Create(http);
        await using var stream = new MemoryStream([1, 2, 3]);

        var result = await client.UploadInputAsync(stream, "clip.mp4", "video/mp4", "refs", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/upload/image", path);
        Assert.Equal("clip.mp4", filename);
        Assert.Equal("video/mp4", contentType);
        Assert.Equal("refs/clip.mp4", result.WorkflowValue);
    }

    [Fact]
    public async Task CancelInterruptsOnlyTheMatchingActivePrompt()
    {
        var requests = new List<string>();
        var handler = new AsyncHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(request.RequestUri.AbsolutePath == "/queue" && request.Method == HttpMethod.Get
                ? Json("""{"queue_running":[[1,"active-prompt",{}]],"queue_pending":[]}""")
                : Json("{}"));
        });
        using var http = new HttpClient(handler);

        var result = await Create(http).CancelAsync("active-prompt", TestContext.Current.CancellationToken);

        Assert.Equal("running_interrupted", result["state"]!.ToString());
        Assert.Contains("POST /interrupt", requests);
        Assert.DoesNotContain("POST /queue", requests);
    }

    [Fact]
    public async Task QueueClearPostsPendingOnlyClearContract()
    {
        string? body = null;
        var handler = new AsyncHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return Json("{}");
        });
        using var http = new HttpClient(handler);

        await Create(http).ClearPendingAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"clear\":true", body);
    }

    private static ComfyClient Create(HttpClient http) =>
        new(http, TestConfiguration.Create(("ComfyWorkflowMcp:BaseUrl", "http://comfy.test:8188")));

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
