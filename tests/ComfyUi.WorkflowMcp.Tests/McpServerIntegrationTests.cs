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
            "setup", "workflow_list", "workflow_get", "workflow_get_options", "generate_image", "generate_audio", "generate_video",
            "comfy_media_upload", "asset_stage", "asset_list", "asset_exists", "asset_view", "comfy_job_cancel", "comfy_queue_list", "comfy_queue_clear", "comfy_diffusion_model_list"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task GenerationToolsExposeOptionalCompletionOverrideWithoutWorkflowRunTools()
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
            Assert.DoesNotContain("wait_for_completion",
                tool["inputSchema"]!["required"]?.AsArray()
                    .Select(value => value!.ToString()) ?? []);
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

    [Fact]
    public async Task SetupSupportsIncrementalConfigurationAndRestoringDefaults()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        async Task<JsonObject> Call(string name, JsonObject arguments)
        {
            var response = await SendMcpAsync(toolClient, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 40,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
            }.ToJsonString());
            return StructuredContent(response);
        }
        async Task Setup(string arguments)
        {
            var values = JsonNode.Parse(arguments)!.AsObject();
            values["workflow_id"] = "test_image";
            var result = await Call("setup", values);
            Assert.True(result["success"]!.GetValue<bool>(), result.ToJsonString());
        }

        await Setup("""{"operation":"parameterize_workflow","parameters":{"aspect_ratio":{"node_id":"1","input_name":"aspect_ratio","type":"string"}}}""");
        await Setup("""{"operation":"configure_workflow","defaults":{"prompt":"hello","aspect_ratio":"16:9"}}""");
        await Setup("""{"operation":"parameterize_workflow","parameters":{"cfg":{"node_id":"1","input_name":"cfg","type":"number"}}}""");
        await Setup("""{"operation":"configure_workflow","defaults":{"cfg":8.5}}""");

        var options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.Equal("hello", options["defaults"]!["prompt"]!.ToString());
        Assert.Equal("16:9", options["defaults"]!["aspect_ratio"]!.ToString());
        Assert.Equal("8.5", options["defaults"]!["cfg"]!.ToString());

        await Setup("""{"operation":"unparameterize_workflow","parameter_names":["cfg"]}""");
        options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.False(options["defaults"]!.AsObject().ContainsKey("cfg"));
        Assert.False(options["parameters"]!.AsObject().ContainsKey("cfg"));
        Assert.Equal("hello", options["defaults"]!["prompt"]!.ToString());
        var workflow = await Call("workflow_get", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.Equal(8.5, workflow["workflow"]!["1"]!["inputs"]!["cfg"]!.GetValue<double>());

        await Setup("""{"operation":"configure_workflow","defaults":{"aspect_ratio":null}}""");
        options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.False(options["defaults"]!.AsObject().ContainsKey("aspect_ratio"));
        Assert.True(options["parameters"]!.AsObject().ContainsKey("aspect_ratio"));
    }

    [Fact]
    public async Task WorkflowListReturnsConfiguredYamlDescription()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        var configured = await SendMcpAsync(toolClient,
            """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"setup","arguments":{"operation":"configure_workflow","workflow_id":"test_image","description":"Generate illustrations with the test image workflow."}}}""");
        Assert.True(StructuredContent(configured)["success"]!.GetValue<bool>(), configured.ToJsonString());

        var response = await SendMcpAsync(toolClient,
            """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"workflow_list","arguments":{}}}""");

        var item = Assert.Single(StructuredContent(response)["workflows"]!.AsArray())!;
        Assert.Equal("Generate illustrations with the test image workflow.", item["workflow"]!["description"]!.ToString());
        Assert.Equal("Generate illustrations with the test image workflow.", item["validation"]!["description"]!.ToString());
    }

    [Fact]
    public async Task DiffusionModelListReturnsModelsReportedByComfyUi()
    {
        await using var comfyUi = new FakeComfyUiServer();
        await comfyUi.StartAsync(TestContext.Current.CancellationToken);
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory(comfyUi.BaseUrl);
        using var toolClient = toolFactory.CreateClient();

        var response = await SendMcpAsync(toolClient,
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"comfy_diffusion_model_list","arguments":{}}}""");

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        Assert.Equal(2, structured["count"]!.GetValue<int>());
        Assert.Equal("flux1-dev.safetensors", structured["models"]![0]!.ToString());
    }

    [Fact]
    public async Task MediaStageListsAndValidatesManagedAssets()
    {
        var staged = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"asset_stage","arguments":{"data":"data:image/png;base64,AQID","filename":"vision.png"}}}""");
        var stagedContent = StructuredContent(staged);
        Assert.True(stagedContent["success"]!.GetValue<bool>());
        var filename = stagedContent["filename"]!.ToString();

        var listed = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"asset_list","arguments":{}}}""");
        var listedContent = StructuredContent(listed);
        Assert.Contains(listedContent["assets"]!.AsArray(), asset => asset!["filename"]!.ToString() == filename);

        var exists = await SendMcpAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 13,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "asset_exists",
                    ["arguments"] = new JsonObject { ["filename"] = filename }
                }
            }.ToJsonString());
        var existsContent = StructuredContent(exists);
        Assert.True(existsContent["exists"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AssetViewReturnsImageBytesAsMcpImageContent()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
        var staged = await SendMcpAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 20,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "asset_stage",
                ["arguments"] = new JsonObject { ["data"] = png, ["filename"] = "reference.png", ["mime_type"] = "image/png" }
            }
        }.ToJsonString());
        var filename = StructuredContent(staged)["filename"]!.ToString();

        var response = await SendMcpAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 21,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "asset_view",
                ["arguments"] = new JsonObject { ["filename"] = filename }
            }
        }.ToJsonString());

        var result = response["result"]!;
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var image = Assert.Single(result["content"]!.AsArray(), item => item!["type"]!.ToString() == "image")!;
        Assert.Equal("image/png", image["mimeType"]!.ToString());
        Assert.Equal(png, image["data"]!.ToString());
        Assert.Equal(filename, StructuredContent(response)["filename"]!.ToString());
        Assert.Equal(Convert.FromBase64String(png).Length, StructuredContent(response)["bytes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("missing.png")]
    [InlineData("../outside.png")]
    public async Task AssetViewFailuresReturnToolErrorsWithoutImageContent(string filename)
    {
        var response = await SendMcpAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 22,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "asset_view",
                ["arguments"] = new JsonObject { ["filename"] = filename }
            }
        }.ToJsonString());

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.All(response["result"]!["content"]!.AsArray(), item => Assert.Equal("text", item!["type"]!.ToString()));
    }

    [Fact]
    public async Task MediaUploadAcceptsOnlyAnAssetFilename()
    {
        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/list","params":{}}""");
        var upload = response["result"]!["tools"]!.AsArray()
            .Single(tool => tool!["name"]!.ToString() == "comfy_media_upload")!;
        var properties = upload["inputSchema"]!["properties"]!.AsObject();

        Assert.Contains("filename", upload["inputSchema"]!["required"]!.AsArray().Select(item => item!.ToString()));
        Assert.DoesNotContain("file_path", properties);
        Assert.DoesNotContain("subfolder", properties);
    }

    [Fact]
    public async Task WorkflowGetRequiresIdAndReturnsParsedJson()
    {
        var toolsResponse = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/list","params":{}}""");
        var tool = toolsResponse["result"]!["tools"]!.AsArray()
            .Single(item => item!["name"]!.ToString() == "workflow_get")!;
        Assert.Contains("workflow_id", tool["inputSchema"]!["required"]!.AsArray()
            .Select(value => value!.ToString()));

        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"workflow_get","arguments":{"workflow_id":"test_image"}}}""");
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        Assert.Equal("test_image", structured["workflow_id"]!.ToString());
        Assert.Equal("SaveImage", structured["workflow"]!["1"]!["class_type"]!.ToString());
    }

    [Fact]
    public async Task WorkflowGetOptionsReturnsYamlAsStructuredData()
    {
        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"workflow_get_options","arguments":{"workflow_id":"test_image"}}}""");
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        Assert.Equal("image", structured["media_type"]!.ToString());
        Assert.Equal("hello", structured["defaults"]!["prompt"]!.ToString());
        Assert.Equal(2, structured["allowed_values"]!["prompt"]!.AsArray().Count);
        Assert.Equal("string", structured["parameters"]!["prompt"]!["type"]!.ToString());
    }

    [Fact]
    public async Task SetupTeachesClientsHowToDiscoverAndParameterizeNodes()
    {
        var toolsResponse = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/list","params":{}}""");
        var setup = toolsResponse["result"]!["tools"]!.AsArray()
            .Single(tool => tool!["name"]!.ToString() == "setup")!;
        Assert.Contains("negative_prompt", setup["description"]!.ToString());
        Assert.Contains("exact top-level JSON key",
            setup["inputSchema"]!["properties"]!["parameters"]!["description"]!.ToString());

        var statusResponse = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"setup","arguments":{"operation":"status"}}}""");
        var structured = statusResponse["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(statusResponse["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        var guide = structured["parameterization_guide"]!;
        Assert.Contains("links another node", guide["json_shape"]!.ToString());
        Assert.Contains("cfg", guide["common_input_names"]!.AsArray().Select(value => value!.ToString()));
        Assert.Equal("integer", guide["parameterize_workflow_example"]!["parameters"]!["steps"]!["type"]!.ToString());
    }

    private Task<JsonObject> SendMcpAsync(string json) => SendMcpAsync(_client, json);

    private static JsonObject StructuredContent(JsonObject response) =>
        response["result"]!["structuredContent"] as JsonObject
        ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();

    private static async Task<JsonObject> SendMcpAsync(HttpClient client, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var data = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonNode.Parse(data)!.AsObject();
    }
}
