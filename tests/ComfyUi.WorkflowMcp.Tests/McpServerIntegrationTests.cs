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
            "setup", "workflow_configure", "workflow_parameterize", "workflow_unparameterize", "workflow_set_default", "workflow_list", "workflow_get", "workflow_get_options", "generate_image", "generate_audio", "generate_video",
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
    public async Task WorkflowListReturnsOnlyCompactSummariesWithoutCallingComfyUi()
    {
        var response = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"workflow_list","arguments":{}}}""");
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        Assert.True(structured["count"]!.GetValue<int>() > 0);
        var item = Assert.Single(structured["workflows"]!.AsArray())!.AsObject();
        string[] summaryFields = ["description", "media_type", "workflow_id"];
        Assert.Equal(summaryFields, item.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("test_image", item["workflow_id"]!.ToString());
        Assert.Equal("image", item["media_type"]!.ToString());
    }

    [Fact]
    public async Task FocusedToolsSupportIncrementalConfigurationAndRestoringDefaults()
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
        async Task<JsonObject> Mutate(string tool, string arguments)
        {
            var values = JsonNode.Parse(arguments)!.AsObject();
            values["workflow_id"] = "test_image";
            var result = await Call(tool, values);
            Assert.True(result["success"]!.GetValue<bool>(), result.ToJsonString());
            return result;
        }

        await Mutate("workflow_parameterize", """{"parameters":{"aspect_ratio":{"node_id":"1","input_name":"aspect_ratio","type":"string"}}}""");
        await Mutate("workflow_configure", """{"defaults":{"prompt":"hello","aspect_ratio":"16:9"}}""");
        await Mutate("workflow_parameterize", """{"parameters":{"cfg":{"node_id":"1","input_name":"cfg","type":"number"}}}""");
        var addedCfg = await Mutate("workflow_configure", """{"defaults":{"cfg":8.5}}""");
        Assert.Equal("cfg", Assert.Single(addedCfg["defaults_updated"]!.AsArray())!.ToString());
        Assert.False(addedCfg.ContainsKey("defaults_removed"));
        var noChange = await Mutate("workflow_parameterize", """{"parameters":{"cfg":{"node_id":"1","input_name":"cfg","type":"number"}}}""");
        Assert.False(noChange.ContainsKey("parameters_updated"));

        var options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.Equal("hello", options["defaults"]!["prompt"]!.ToString());
        Assert.Equal("16:9", options["defaults"]!["aspect_ratio"]!.ToString());
        Assert.Equal("8.5", options["defaults"]!["cfg"]!.ToString());

        var restored = await Mutate("workflow_unparameterize", """{"parameter_names":["cfg"]}""");
        Assert.Equal("cfg", Assert.Single(restored["parameters_restored"]!.AsArray())!.ToString());
        Assert.Equal("cfg", Assert.Single(restored["defaults_removed"]!.AsArray())!.ToString());
        options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.False(options["defaults"]!.AsObject().ContainsKey("cfg"));
        Assert.False(options["parameters"]!.AsObject().ContainsKey("cfg"));
        Assert.Equal("hello", options["defaults"]!["prompt"]!.ToString());
        var workflow = await Call("workflow_get", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.Equal(8.5, workflow["workflow"]!["1"]!["inputs"]!["cfg"]!.GetValue<double>());

        await Mutate("workflow_configure", """{"defaults":{"aspect_ratio":null}}""");
        options = await Call("workflow_get_options", new JsonObject { ["workflow_id"] = "test_image" });
        Assert.False(options["defaults"]!.AsObject().ContainsKey("aspect_ratio"));
        Assert.True(options["parameters"]!.AsObject().ContainsKey("aspect_ratio"));
    }

    [Fact]
    public async Task ConfigurationReceiptsReportActualChangesAndRemovals()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        async Task<JsonObject> Configure(string changes)
        {
            var arguments = JsonNode.Parse(changes)!.AsObject();
            arguments["workflow_id"] = "test_image";
            return await CallStructuredAsync(toolClient, "workflow_configure", arguments);
        }

        var receipt = await Configure("""{"defaults":{"prompt":"goodbye"},"allowed_values":{"prompt":["goodbye"]},"constraints":{"prompt":{"pattern":".+"}},"description":"Example","wait_for_completion":false}""");
        Assert.True(receipt["success"]!.GetValue<bool>());
        foreach (var field in new[] { "defaults_updated", "allowed_values_updated", "constraints_updated" })
            Assert.Equal("prompt", Assert.Single(receipt[field]!.AsArray())!.ToString());
        Assert.Equal(2, receipt["fields_updated"]!.AsArray().Count);
        Assert.False(receipt.ContainsKey("path"));

        receipt = await Configure("""{"defaults":{"prompt":"goodbye"},"description":"Example"}""");
        Assert.Equal(2, receipt.Count);
        Assert.True(receipt["success"]!.GetValue<bool>());

        receipt = await Configure("""{"defaults":{"prompt":null},"allowed_values":{"prompt":null},"constraints":{"prompt":null},"description":""}""");
        foreach (var field in new[] { "defaults_removed", "allowed_values_removed", "constraints_removed" })
            Assert.Equal("prompt", Assert.Single(receipt[field]!.AsArray())!.ToString());
        Assert.Equal("description", Assert.Single(receipt["fields_removed"]!.AsArray())!.ToString());
        Assert.False(receipt.ContainsKey("defaults_updated"));

        receipt = await Configure("""{"defaults":{"unknown":1}}""");
        Assert.False(receipt["success"]!.GetValue<bool>());
        Assert.Contains("no parameter", receipt["error"]!.ToString());
        Assert.False(receipt.ContainsKey("defaults_updated"));
    }

    [Fact]
    public async Task ParameterizationReceiptsUsePublicNamesAndRejectInvalidNamesBeforeWriting()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        foreach (var name in new[] { "", "123" })
        {
            var result = await CallStructuredAsync(toolClient, "workflow_parameterize", new JsonObject
            {
                ["workflow_id"] = "test_image",
                ["parameters"] = new JsonObject
                {
                    [name] = new JsonObject { ["node_id"] = "1", ["input_name"] = "cfg", ["type"] = "number" }
                }
            });
            Assert.False(result["success"]!.GetValue<bool>());
            var unchanged = await CallStructuredAsync(toolClient, "workflow_get", new JsonObject { ["workflow_id"] = "test_image" });
            Assert.Equal(7, unchanged["workflow"]!["1"]!["inputs"]!["cfg"]!.GetValue<double>());
        }
        var receipt = await CallStructuredAsync(toolClient, "workflow_parameterize", new JsonObject
        {
            ["workflow_id"] = "test_image",
            ["parameters"] = new JsonObject
            {
                ["CFG Scale"] = new JsonObject { ["node_id"] = "1", ["input_name"] = "cfg", ["type"] = "number" }
            }
        });
        Assert.True(receipt["success"]!.GetValue<bool>());
        Assert.Equal("cfg_scale", Assert.Single(receipt["parameters_updated"]!.AsArray())!.ToString());
    }

    [Fact]
    public async Task FocusedDefaultToolPreservesDefaultSelectionAndReportsNoOp()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        var arguments = new JsonObject { ["workflow_id"] = "test_image", ["media_type"] = "image" };
        var receipt = await CallStructuredAsync(toolClient, "workflow_set_default", arguments);
        Assert.True(receipt["success"]!.GetValue<bool>());
        Assert.True(receipt["default_updated"]!.GetValue<bool>());
        receipt = await CallStructuredAsync(toolClient, "workflow_set_default", arguments);
        Assert.False(receipt["default_updated"]!.GetValue<bool>());
        var status = await CallStructuredAsync(toolClient, "setup", []);
        Assert.Equal("test_image", status["default_workflows"]!["image"]!.ToString());

        receipt = await CallStructuredAsync(toolClient, "workflow_set_default",
            new JsonObject { ["workflow_id"] = "missing", ["media_type"] = "image" });
        Assert.False(receipt["success"]!.GetValue<bool>());
        status = await CallStructuredAsync(toolClient, "setup", []);
        Assert.Equal("test_image", status["default_workflows"]!["image"]!.ToString());
    }

    [Fact]
    public async Task WorkflowMutationSchemasAreFocusedAndAssetStagingIsClientSupplied()
    {
        var response = await SendMcpAsync("""{"jsonrpc":"2.0","id":41,"method":"tools/list","params":{}}""");
        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "workflow_configure", "workflow_parameterize", "workflow_unparameterize", "workflow_set_default" })
        {
            var schema = tools.Single(tool => tool!["name"]!.ToString() == name)!["inputSchema"]!;
            Assert.DoesNotContain("operation", schema["properties"]!.AsObject());
            Assert.Contains("workflow_id", schema["required"]!.AsArray().Select(item => item!.ToString()));
            if (name != "workflow_configure")
            {
                Assert.Equal(2, schema["properties"]!.AsObject().Count);
                Assert.Equal(2, schema["required"]!.AsArray().Count);
            }
        }
        var stage = tools.Single(tool => tool!["name"]!.ToString() == "asset_stage")!;
        Assert.Contains("Never generate or reproduce base64", stage["description"]!.ToString());
        Assert.Contains("supplied directly by the client", stage["inputSchema"]!["properties"]!["data"]!["description"]!.ToString());
    }

    [Fact]
    public async Task WorkflowListReturnsConfiguredYamlDescription()
    {
        using var toolFactory = new ComfyWorkflowMcpApplicationFactory();
        using var toolClient = toolFactory.CreateClient();
        var configured = await SendMcpAsync(toolClient,
            """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"workflow_configure","arguments":{"workflow_id":"test_image","description":"Generate illustrations with the test image workflow."}}}""");
        Assert.True(StructuredContent(configured)["success"]!.GetValue<bool>(), configured.ToJsonString());

        var response = await SendMcpAsync(toolClient,
            """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"workflow_list","arguments":{}}}""");

        var item = Assert.Single(StructuredContent(response)["workflows"]!.AsArray())!;
        Assert.Equal("Generate illustrations with the test image workflow.", item["description"]!.ToString());
        Assert.False(item.AsObject().ContainsKey("validation"));
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
        Assert.Empty(setup["inputSchema"]!["properties"]!.AsObject());
        var parameterize = toolsResponse["result"]!["tools"]!.AsArray()
            .Single(tool => tool!["name"]!.ToString() == "workflow_parameterize")!;
        Assert.Contains("exact top-level JSON key",
            parameterize["inputSchema"]!["properties"]!["parameters"]!["description"]!.ToString());

        var statusResponse = await SendMcpAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"setup","arguments":{}}}""");
        var structured = statusResponse["result"]!["structuredContent"] as JsonObject
            ?? JsonNode.Parse(statusResponse["result"]!["content"]![0]!["text"]!.ToString())!.AsObject();
        var guide = structured["parameterization_guide"]!;
        Assert.Contains("links another node", guide["json_shape"]!.ToString());
        Assert.Contains("cfg", guide["common_input_names"]!.AsArray().Select(value => value!.ToString()));
        Assert.Equal("workflow_parameterize", guide["parameterize_example"]!["tool"]!.ToString());
        Assert.Equal("number", guide["parameterize_example"]!["arguments"]!["parameters"]!["cfg"]!["type"]!.ToString());
        Assert.Contains("never generate or reproduce base64", structured["asset_guide"]!.ToString());
        Assert.False(structured.ContainsKey("workflows"));
    }

    private Task<JsonObject> SendMcpAsync(string json) => SendMcpAsync(_client, json);

    private static async Task<JsonObject> CallStructuredAsync(HttpClient client, string tool, JsonObject arguments) =>
        StructuredContent(await SendMcpAsync(client, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 42,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }
        }.ToJsonString()));

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
