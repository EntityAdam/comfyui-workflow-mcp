using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowPreparationTests
{
    internal const string ReferenceGraph = """
        {
          "clip":{"class_type":"CLIPLoader","inputs":{}},
          "vae":{"class_type":"VAELoader","inputs":{}},
          "audio_vae":{"class_type":"VAELoader","inputs":{}},
          "136":{"class_type":"MiniMaxH3ReferenceToVideo","inputs":{
            "clip":["clip",0],"vae":["vae",0],"audio_vae":["audio_vae",0],
            "prompt":"PARAM_PROMPT","width":1344,"height":768,"length":124,"ref_image_size":"match"
          }},
          "output":{"class_type":"Consumer","inputs":{"conditioning":["136",0],"latent":["136",1]}}
        }
        """;
    internal const string ReferenceYaml = """
        media_type: image
        transforms:
          - id: refs
            kind: minimax_references
            node_id: "136"
        """;
    private const string SeedYaml = """
        transforms:
          - id: seed_compatibility
            kind: replace_negative_rgthree_seed
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeSeedReplacementPreservesIdsConnectionsAndTemplate(bool parameterized)
    {
        await using var server = new FakeComfyUiServer();
        AddDefinitions(server);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new PreparationFixture(server.BaseUrl, """
            {"1":{"class_type":"Seed (rgthree)","inputs":{"seed":-1},"_meta":{"custom":"retained"}},
             "2":{"class_type":"Seed (rgthree)","inputs":{"seed":17}},
             "3":{"class_type":"Consumer","inputs":{"seed":["1",0]}}}
            """, SeedYaml);
        if (parameterized)
        {
            var graph = fixture.Workflows.Load("test")!;
            graph["1"]!["inputs"]!["seed"] = "PARAM_INT_SEED";
            fixture.WriteGraph(graph);
        }
        var original = File.ReadAllText(fixture.JsonPath);
        var prepared = await fixture.Preparation.PrepareAsync("test", parameterized ? new JsonObject { ["seed"] = -1 } : [], TestContext.Current.CancellationToken);
        Assert.Equal("SeedNode", prepared.Workflow["1"]!["class_type"]!.ToString());
        Assert.Equal(-1, prepared.Workflow["1"]!["inputs"]!["seed"]!.GetValue<int>());
        Assert.Equal("retained", prepared.Workflow["1"]!["_meta"]!["custom"]!.ToString());
        Assert.Equal("Seed (rgthree)", prepared.Workflow["2"]!["class_type"]!.ToString());
        Assert.Equal("1", prepared.Workflow["3"]!["inputs"]!["seed"]![0]!.ToString());
        Assert.Single(prepared.Changes);
        Assert.Equal(original, File.ReadAllText(fixture.JsonPath));
        Assert.Null(server.LastPromptBody);
    }

    [Fact]
    public async Task OmittedSeedKeepsExistingRandomizationAndNeedsNoNodeLookup()
    {
        await using var server = new FakeComfyUiServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new PreparationFixture(server.BaseUrl,
            """{"1":{"class_type":"Seed (rgthree)","inputs":{"seed":"PARAM_INT_SEED"}}}""", SeedYaml);
        var prepared = await fixture.Preparation.PrepareAsync("test", [], TestContext.Current.CancellationToken);
        Assert.Equal("Seed (rgthree)", prepared.Workflow["1"]!["class_type"]!.ToString());
        Assert.True(prepared.Workflow["1"]!["inputs"]!["seed"]!.GetValue<long>() >= 0);
        Assert.Empty(server.NodeDefinitionRequests);
    }

    [Fact]
    public async Task OrderedMediaBuildsLoadersAndKeepsSparseVideoSoundtracksAligned()
    {
        await using var server = new FakeComfyUiServer();
        AddDefinitions(server);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new PreparationFixture(server.BaseUrl, ReferenceGraph, ReferenceYaml);
        var original = File.ReadAllText(fixture.JsonPath);
        var values = JsonNode.Parse("""
            {"prompt":"<Picture 1> in <Video 2>","reference_images":["folder/first.png","second.png"],
             "reference_videos":[{"file":"silent.mp4"},{"file":"speaking.mp4","include_audio":true}],
             "reference_audios":["voice.wav","music.wav"]}
            """)!.AsObject();
        var prepared = await fixture.Preparation.PrepareAsync("test", values, TestContext.Current.CancellationToken);
        var graph = prepared.Workflow;
        var inputs = graph["136"]!["inputs"]!;
        Assert.Equal("<Picture 1> in <Video 2>", inputs["prompt"]!.ToString());
        Assert.Equal("folder/first.png", Source(graph, inputs["ref_images.ref_image_0"]!)["inputs"]!["image"]!.ToString());
        Assert.Equal("second.png", Source(graph, inputs["ref_images.ref_image_1"]!)["inputs"]!["image"]!.ToString());
        Assert.Null(inputs["ref_video_audios.ref_video_audio_0"]);
        var frames = inputs["ref_videos.ref_video_1"]!;
        var soundtrack = inputs["ref_video_audios.ref_video_audio_1"]!;
        Assert.Equal(frames[0]!.ToString(), soundtrack[0]!.ToString());
        Assert.Equal(0, frames[1]!.GetValue<int>());
        Assert.Equal(1, soundtrack[1]!.GetValue<int>());
        var components = Source(graph, frames);
        Assert.Equal("speaking.mp4", Source(graph, components["inputs"]!["video"]!)["inputs"]!["file"]!.ToString());
        Assert.Equal("voice.wav", Source(graph, inputs["ref_audios.ref_audio_0"]!)["inputs"]!["audio"]!.ToString());
        Assert.Equal("music.wav", Source(graph, inputs["ref_audios.ref_audio_1"]!)["inputs"]!["audio"]!.ToString());
        Assert.Equal("136", graph["output"]!["inputs"]!["latent"]![0]!.ToString());
        Assert.Equal(13, graph.Count);
        Assert.Equal(5, server.NodeDefinitionRequests.Count);
        Assert.Equal(original, File.ReadAllText(fixture.JsonPath));
        Assert.Equal(2, values["reference_images"]!.AsArray().Count);
        Assert.Null(server.LastPromptBody);
    }

    [Fact]
    public async Task RebuildingRemovesOwnedLoadersAndPreservesCollidingUserNode()
    {
        await using var server = new FakeComfyUiServer();
        AddDefinitions(server);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new PreparationFixture(server.BaseUrl, ReferenceGraph, ReferenceYaml);
        var graph = fixture.Workflows.Load("test")!;
        graph["mcp:refs:image:0"] = JsonNode.Parse("""{"class_type":"LoadImage","inputs":{"image":"user.png"}}""");
        fixture.WriteGraph(graph);
        var options = JsonNode.Parse("""{"prompt":"hello","reference_images":["one.png","two.png"]}""")!.AsObject();
        var first = await fixture.Preparation.PrepareAsync("test", options, TestContext.Current.CancellationToken);
        fixture.WriteGraph(first.Workflow);
        var repeatOptions = options.DeepClone().AsObject();
        repeatOptions.Remove("prompt"); // The persisted rendered graph has a literal prompt.
        var second = await fixture.Preparation.PrepareAsync("test", repeatOptions, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(first.Workflow, second.Workflow));
        fixture.WriteGraph(second.Workflow);
        var cleared = await fixture.Preparation.PrepareAsync("test", [], TestContext.Current.CancellationToken);
        Assert.Equal(6, cleared.Workflow.Count);
        Assert.Equal("user.png", cleared.Workflow["mcp:refs:image:0"]!["inputs"]!["image"]!.ToString());
        Assert.Null(cleared.Workflow["136"]!["inputs"]!["ref_images.ref_image_0"]);
        Assert.Equal(2, cleared.Changes.Count(change => change!["operation"]!.ToString() == "remove"));
    }

    [Theory]
    [InlineData("reference_images", "null")]
    [InlineData("reference_images", "[null]")]
    [InlineData("reference_images", "[123]")]
    [InlineData("reference_images", """[" "]""")]
    [InlineData("reference_audios", "[\"a\",\"b\",\"c\",\"d\"]")]
    [InlineData("reference_videos", "[\"video.mp4\"]")]
    [InlineData("reference_videos", "[{\"file\":\"a.mp4\",\"include_audio\":\"yes\"}]")]
    [InlineData("reference_videos", "[{\"file\":\"a.mp4\",\"typo\":true}]")]
    public async Task InvalidMediaFailsBeforeContactingComfyUi(string name, string json)
    {
        using var fixture = new PreparationFixture("http://127.0.0.1:1", ReferenceGraph, ReferenceYaml);
        var values = new JsonObject { ["prompt"] = "hello", [name] = JsonNode.Parse(json) };
        var error = await Assert.ThrowsAsync<GenerationParameterException>(() => fixture.Preparation.PrepareAsync("test", values, TestContext.Current.CancellationToken));
        Assert.Equal(name, error.Parameter);
    }

    [Fact]
    public async Task MissingReplacementAndChangedInstalledLimitsFailWithoutSubmission()
    {
        await using var server = new FakeComfyUiServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var seed = new PreparationFixture(server.BaseUrl,
            """{"1":{"class_type":"Seed (rgthree)","inputs":{"seed":-1}}}""", SeedYaml);
        var missing = await Assert.ThrowsAsync<ToolFailureException>(() => seed.Preparation.PrepareAsync("test", [], TestContext.Current.CancellationToken));
        Assert.Equal("node_unavailable", missing.Code);
        AddDefinitions(server);
        server.NodeDefinitions["MiniMaxH3ReferenceToVideo"]!["input"]!["optional"]!["ref_images"]![1]!["template"]!["max"] = 1;
        using var references = new PreparationFixture(server.BaseUrl, ReferenceGraph, ReferenceYaml);
        var options = JsonNode.Parse("""{"prompt":"hello","reference_images":["a.png","b.png"]}""")!.AsObject();
        var exceeded = await Assert.ThrowsAsync<ToolFailureException>(() => references.Preparation.PrepareAsync("test", options, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_transform", exceeded.Code);
        Assert.Null(server.LastPromptBody);
    }

    [Fact]
    public void MediaOptionsAreDiscoveredOnlyWhenTransformIsEnabled()
    {
        using var fixture = new PreparationFixture("http://127.0.0.1:1", ReferenceGraph, ReferenceYaml);
        var options = JsonSerializer.SerializeToNode(fixture.Options.GetOptions("test"))!;
        Assert.Equal("array", options["parameters"]!["reference_images"]!["type"]!.ToString());
        Assert.False(options["parameters"]!["reference_images"]!["required"]!.GetValue<bool>());
        Assert.Equal(9, options["parameters"]!["reference_images"]!["maxItems"]!.GetValue<int>());
        fixture.Configuration.SaveWorkflow("test", null, null, null, null, null, transforms: []);
        var error = Assert.Throws<ToolFailureException>(() => fixture.Options.Resolve("test", new JsonObject
        {
            ["prompt"] = "hello",
            ["reference_images"] = new JsonArray("a.png")
        }));
        Assert.Equal("unknown_parameter", error.Code);
    }

    [Theory]
    [InlineData("[{\"id\":\"x\",\"kind\":\"unknown\"}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"minimax_references\"}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"replace_negative_rgthree_seed\",\"enabled\":1}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"replace_negative_rgthree_seed\"},{\"id\":\"x\",\"kind\":\"replace_negative_rgthree_seed\"}]")]
    public void InvalidTransformConfigurationLeavesYamlUnchanged(string json)
    {
        using var fixture = new PreparationFixture("http://127.0.0.1:1", ReferenceGraph, ReferenceYaml);
        var before = File.ReadAllText(fixture.YamlPath);
        Assert.Throws<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, null, null, null, null,
            transforms: JsonNode.Parse(json)!.AsArray()));
        Assert.Equal(before, File.ReadAllText(fixture.YamlPath));
    }

    [Theory]
    [InlineData("""{"1":{"inputs":{"model":["missing",0]}}}""")]
    [InlineData("""{"1":{"inputs":{"model":["2",0]}},"2":{"inputs":{"model":["1",0]}}}""")]
    [InlineData("""{"1":{"inputs":{"model":["2",-1]}},"2":{"inputs":{}}}""")]
    public void PreparedGraphRejectsDanglingLinksNegativeSlotsAndCycles(string json)
    {
        Assert.Throws<ToolFailureException>(() => WorkflowPreparationService.ValidateGraph(JsonNode.Parse(json)!.AsObject()));
    }

    private static JsonObject Source(JsonObject graph, JsonNode link) => graph[link[0]!.ToString()]!.AsObject();

    [Fact]
    public void ConfigurationRoundTripsQuotedIdsAndMediaNames()
    {
        using var fixture = new PreparationFixture("http://127.0.0.1:1",
            ReferenceGraph.Replace("\"136\"", "\"001\"", StringComparison.Ordinal),
            ReferenceYaml.Replace("\"136\"", "\"001\"", StringComparison.Ordinal));
        Assert.Equal("001", fixture.Configuration.Load("test").Transforms[0]!["node_id"]!.GetValue<string>());
        fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["reference_images"] = new JsonArray("123", "true") }, null, null);
        var loaded = fixture.Configuration.Load("test");
        Assert.Equal("001", loaded.Transforms[0]!["node_id"]!.GetValue<string>());
        Assert.Equal("123", loaded.Defaults["reference_images"]![0]!.GetValue<string>());
        Assert.Equal("true", loaded.Defaults["reference_images"]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task SharedGeneratedLoaderCannotBeRemovedByReferenceRebuild()
    {
        await using var server = new FakeComfyUiServer();
        AddDefinitions(server);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var fixture = new PreparationFixture(server.BaseUrl, ReferenceGraph, ReferenceYaml);
        var first = await fixture.Preparation.PrepareAsync("test", new JsonObject
        {
            ["prompt"] = "hello",
            ["reference_images"] = new JsonArray("one.png")
        }, TestContext.Current.CancellationToken);
        first.Workflow["output"]!["inputs"]!["shared_image"] = new JsonArray("mcp:refs:image:0", 0);
        fixture.WriteGraph(first.Workflow);
        var before = File.ReadAllText(fixture.JsonPath);
        var error = await Assert.ThrowsAsync<ToolFailureException>(() => fixture.Preparation.PrepareAsync("test", [], TestContext.Current.CancellationToken));
        Assert.Equal("invalid_transform", error.Code);
        Assert.Equal(before, File.ReadAllText(fixture.JsonPath));
        Assert.Null(server.LastPromptBody);
    }

    [Fact]
    public async Task IncompatibleSeedOutputAndMissingClipFailBeforeSubmission()
    {
        await using var server = new FakeComfyUiServer();
        AddDefinitions(server);
        await server.StartAsync(TestContext.Current.CancellationToken);
        server.NodeDefinitions["SeedNode"]!["output"] = new JsonArray("FLOAT");
        using var seed = new PreparationFixture(server.BaseUrl,
            """{"1":{"class_type":"Seed (rgthree)","inputs":{"seed":-1}}}""", SeedYaml);
        var outputError = await Assert.ThrowsAsync<ToolFailureException>(() => seed.Preparation.PrepareAsync("test", [], TestContext.Current.CancellationToken));
        Assert.Equal("invalid_transform", outputError.Code);
        using var references = new PreparationFixture(server.BaseUrl, ReferenceGraph, ReferenceYaml);
        var graph = references.Workflows.Load("test")!;
        graph["136"]!["inputs"]!.AsObject().Remove("clip");
        references.WriteGraph(graph);
        var clipError = await Assert.ThrowsAsync<ToolFailureException>(() => references.Preparation.PrepareAsync("test", new JsonObject { ["prompt"] = "hello" }, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_transform", clipError.Code);
        Assert.Contains("clip", clipError.Message, StringComparison.Ordinal);
        Assert.Null(server.LastPromptBody);
    }

    internal static void AddDefinitions(FakeComfyUiServer server)
    {
        server.NodeDefinitions["SeedNode"] = JsonNode.Parse("""{"input":{"required":{"seed":["INT",{"min":-1}]}},"output":["INT"]}""");
        server.NodeDefinitions["LoadImage"] = JsonNode.Parse("""{"input":{"required":{"image":["COMBO",{}]}},"output":["IMAGE","MASK"]}""");
        server.NodeDefinitions["LoadAudio"] = JsonNode.Parse("""{"input":{"required":{"audio":["COMBO",{}]}},"output":["AUDIO"]}""");
        server.NodeDefinitions["LoadVideo"] = JsonNode.Parse("""{"input":{"required":{"file":["COMBO",{}]}},"output":["VIDEO"]}""");
        server.NodeDefinitions["GetVideoComponents"] = JsonNode.Parse("""{"input":{"required":{"video":["VIDEO",{}]}},"output":["IMAGE","AUDIO","FLOAT"]}""");
        // ComfyUI V3 serializes Autogrow.TemplatePrefix under input.optional.<group>[1].template.
        server.NodeDefinitions["MiniMaxH3ReferenceToVideo"] = JsonNode.Parse("""
            {"input":{"required":{"clip":["CLIP",{}],"prompt":["STRING",{}],"width":["INT",{}],"height":["INT",{}],"length":["INT",{}],"ref_image_size":[["match","max"],{}]},
             "optional":{
              "ref_images":["COMFY_AUTOGROW_V3",{"template":{"input":{"required":{"ref_image":["IMAGE",{}]}},"prefix":"ref_image_","min":0,"max":9}}],
              "ref_videos":["COMFY_AUTOGROW_V3",{"template":{"input":{"required":{"ref_video":["IMAGE",{}]}},"prefix":"ref_video_","min":0,"max":3}}],
              "ref_video_audios":["COMFY_AUTOGROW_V3",{"template":{"input":{"required":{"ref_video_audio":["AUDIO",{}]}},"prefix":"ref_video_audio_","min":0,"max":3}}],
              "ref_audios":["COMFY_AUTOGROW_V3",{"template":{"input":{"required":{"ref_audio":["AUDIO",{}]}},"prefix":"ref_audio_","min":0,"max":3}}]
             }},"output":["CONDITIONING","LATENT"]}
            """);
    }

    private sealed class PreparationFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly HttpClient _http = new();
        internal string JsonPath { get; }
        internal string YamlPath { get; }
        internal WorkflowManager Workflows { get; }
        internal WorkflowConfigurationManager Configuration { get; }
        internal GenerationOptionsService Options { get; }
        internal WorkflowPreparationService Preparation { get; }

        internal PreparationFixture(string baseUrl, string graph, string yaml)
        {
            var directory = Path.Combine(_temp.Path, "workflows");
            Directory.CreateDirectory(directory);
            JsonPath = Path.Combine(directory, "test.json");
            YamlPath = Path.Combine(directory, "test.yaml");
            File.WriteAllText(JsonPath, graph);
            File.WriteAllText(YamlPath, yaml);
            var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", _temp.Path), ("ComfyWorkflowMcp:BaseUrl", baseUrl));
            Configuration = new WorkflowConfigurationManager(config);
            Workflows = new WorkflowManager(config, Configuration);
            Options = new GenerationOptionsService(Workflows, Configuration);
            Preparation = new WorkflowPreparationService(Workflows, Configuration, Options, new ComfyClient(_http, config));
        }

        internal void WriteGraph(JsonObject graph) => File.WriteAllText(JsonPath, graph.ToJsonString());
        public void Dispose()
        {
            _http.Dispose();
            _temp.Dispose();
        }
    }
}
