using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class ComfyClientTests
{
    internal const string Rejection = """
        {"error":{"type":"prompt_outputs_failed_validation","message":"Prompt outputs failed validation","details":"","extra_info":{}},
         "node_errors":{"sampler:7":{"class_type":"KSampler","dependent_outputs":["9"],"errors":[
           {"type":"value_smaller_than_min","message":"Value smaller than min","details":"steps: 0 < 1",
            "extra_info":{"input_name":"steps","input_config":["INT",{"min":1}],"received_value":0}},
           {"type":"required_input_missing","message":"Required input is missing","details":"model","extra_info":{"input_name":"model"}}
         ]}}}
        """;

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task WorkflowRejectionsRetainStructuredNodeAndInputDetails(HttpStatusCode status, bool acceptedBranches)
    {
        var body = JsonNode.Parse(Rejection)!.AsObject();
        if (acceptedBranches) body["prompt_id"] = "accepted-branch";
        var expected = body.DeepClone();
        var requests = 0;
        using var http = new HttpClient(new AsyncHandler(_ =>
        {
            requests++;
            var response = Json(body.ToJsonString());
            response.StatusCode = status;
            return Task.FromResult(response);
        }));
        var failure = await Assert.ThrowsAsync<ComfyWorkflowRejectedException>(() => Create(http).SubmitAsync([], cancellationToken: TestContext.Current.CancellationToken));
        var error = ToolErrors.Describe(failure, "fallback");
        Assert.Equal("workflow_rejected", error["error"]!.ToString());
        Assert.Equal((int)status, error["status_code"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expected["node_errors"], error["node_errors"]));
        Assert.True(JsonNode.DeepEquals(expected["error"], error["comfy_error"]));
        var validation = error["validation_errors"]!.AsArray();
        Assert.Equal(2, validation.Count);
        Assert.Equal("sampler:7", validation[0]!["node_id"]!.ToString());
        Assert.Equal("KSampler", validation[0]!["class_type"]!.ToString());
        Assert.Equal("steps", validation[0]!["input_name"]!.ToString());
        Assert.Equal("value_smaller_than_min", validation[0]!["type"]!.ToString());
        Assert.Equal("steps: 0 < 1", validation[0]!["details"]!.ToString());
        Assert.Equal(0, validation[0]!["extra_info"]!["received_value"]!.GetValue<int>());
        Assert.Equal(acceptedBranches, error["may_have_queued"]!.GetValue<bool>());
        if (acceptedBranches)
        {
            Assert.Equal("accepted-branch", error["prompt_id"]!.ToString());
            Assert.Contains("before resubmitting", error["next_step"]!.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task NonJsonHttpFailureRetainsStatusAndRejectionCode()
    {
        using var http = new HttpClient(new AsyncHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Upstream unavailable", Encoding.UTF8, "text/plain")
        })));
        var failure = await Assert.ThrowsAsync<ComfyWorkflowRejectedException>(() => Create(http).SubmitAsync([], cancellationToken: TestContext.Current.CancellationToken));
        var error = ToolErrors.Describe(failure, "fallback");
        Assert.Equal("workflow_rejected", error["error"]!.ToString());
        Assert.Equal(503, error["status_code"]!.GetValue<int>());
        Assert.Equal("Upstream unavailable", error["response_body"]!.ToString());
    }

    [Fact]
    public async Task DiffusionModelListUsesComfyUiModelsEndpoint()
    {
        string? path = null;
        var handler = new AsyncHandler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(Json("""["flux1-dev.safetensors","sd3.5_large.safetensors"]"""));
        });
        using var http = new HttpClient(handler);

        var models = await Create(http).GetDiffusionModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/models/diffusion_models", path);
        Assert.Equal(["flux1-dev.safetensors", "sd3.5_large.safetensors"], models);
    }

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
