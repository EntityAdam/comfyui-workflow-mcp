using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowConfigurationValidationTests
{
    private const string Graph = """
        {"1":{"class_type":"Inputs","inputs":{"prompt":"PARAM_PROMPT","steps":"PARAM_INT_STEPS",
        "cfg":"PARAM_FLOAT_CFG","enabled":"PARAM_BOOL_ENABLED","seed":"PARAM_INT_SEED"}}}
        """;

    [Theory]
    [InlineData("defaults", """{"steps":"bad"}""", "invalid_default")]
    [InlineData("defaults", """{"steps":10.5}""", "invalid_default")]
    [InlineData("defaults", """{"steps":[10]}""", "invalid_default")]
    [InlineData("defaults", """{"prompt":{"text":"cat"}}""", "invalid_default")]
    [InlineData("defaults", """{"cfg":"NaN"}""", "invalid_default")]
    [InlineData("defaults", """{"prompt":"PARAM_PROMPT"}""", "invalid_default")]
    [InlineData("allowed_values", """{"steps":["bad"]}""", "invalid_configuration")]
    [InlineData("allowed_values", """{"steps":10}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"minimum":20,"maximum":10}}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"maximum":"many"}}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"maximun":10}}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"required":"false"}}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"required":false}}""", "invalid_configuration")]
    [InlineData("constraints", """{"steps":{"pattern":".+"}}""", "invalid_configuration")]
    [InlineData("constraints", """{"prompt":{"pattern":"["}}""", "invalid_configuration")]
    public void InvalidConfigurationLeavesExistingJsonAndYamlUntouched(string section, string change, string code)
    {
        using var fixture = new Fixture(Graph);
        fixture.Configuration.SaveWorkflow("test", "image", null, new JsonObject { ["prompt"] = "cat" }, null, null);
        var beforeJson = File.ReadAllText(fixture.JsonPath);
        var beforeYaml = File.ReadAllText(fixture.YamlPath);
        var update = JsonNode.Parse(change)!.AsObject();
        var error = Assert.ThrowsAny<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, false,
            section == "defaults" ? update : null,
            section == "allowed_values" ? update : null,
            section == "constraints" ? update : null, "Changed"));
        Assert.Equal(code, error.Code);
        Assert.Equal(beforeJson, File.ReadAllText(fixture.JsonPath));
        Assert.Equal(beforeYaml, File.ReadAllText(fixture.YamlPath));
    }

    [Fact]
    public void ChangedRulesValidateExistingDefaultsAndCanBeRepairedInSameUpdate()
    {
        using var fixture = new Fixture(Graph);
        fixture.Configuration.SaveWorkflow("test", null, null, new JsonObject { ["steps"] = 10 }, null, null);
        var before = File.ReadAllText(fixture.YamlPath);
        var rules = JsonNode.Parse("""{"steps":{"minimum":1,"maximum":5}}""")!.AsObject();
        var error = Assert.Throws<GenerationParameterException>(() => fixture.Configuration.SaveWorkflow("test", null, null, null, null, rules));
        Assert.Equal("invalid_default", error.Code);
        Assert.Equal(before, File.ReadAllText(fixture.YamlPath));
        fixture.Configuration.SaveWorkflow("test", null, null, new JsonObject { ["steps"] = 4 }, new JsonObject { ["steps"] = new JsonArray(4, 5) }, rules);
        Assert.Equal("4", fixture.Configuration.Load("test").Defaults["steps"]!.ToString());
        Assert.Throws<GenerationParameterException>(() => fixture.Configuration.SaveWorkflow("test", null, null,
            null, new JsonObject { ["steps"] = new JsonArray(5) }, null));
    }

    [Fact]
    public void RequiredFlagsMatchActualCallerRequirementsIncludingSeedFallback()
    {
        using var fixture = new Fixture(Graph);
        fixture.Configuration.SaveWorkflow("test", null, null,
            JsonNode.Parse("""{"prompt":"cat","steps":"10","cfg":7,"enabled":false}""")!.AsObject(), null,
            JsonNode.Parse("""{"prompt":{"required":true},"steps":{"required":false},"seed":{"required":false}}""")!.AsObject());
        var options = JsonSerializer.SerializeToNode(fixture.Options.GetOptions("test"))!;
        foreach (var name in new[] { "prompt", "steps", "cfg", "enabled", "seed" })
            Assert.False(options["parameters"]![name]!["required"]!.GetValue<bool>());
        var resolved = fixture.Options.Resolve("test", []);
        Assert.Equal(10, resolved.Values["steps"]!.GetValue<long>());
        Assert.True(resolved.Values["seed"]!.GetValue<long>() >= 0);
        Assert.Throws<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["steps"] = null }, null, null));
        fixture.Configuration.SaveWorkflow("test", null, null, new JsonObject { ["steps"] = null }, null,
            JsonNode.Parse("""{"steps":{"required":null}}""")!.AsObject());
        options = JsonSerializer.SerializeToNode(fixture.Options.GetOptions("test"))!;
        Assert.True(options["parameters"]!["steps"]!["required"]!.GetValue<bool>());
        var missing = Assert.Throws<GenerationParameterException>(() => fixture.Options.Resolve("test", []));
        Assert.Equal("missing_default", missing.Code);
        Assert.Equal("steps", missing.Parameter);
    }

    [Fact]
    public void ExternalOptionalScalarWithoutFallbackFailsDiscoveryAndResolution()
    {
        using var fixture = new Fixture(Graph);
        File.WriteAllText(fixture.YamlPath, "constraints:\n  steps:\n    required: false\n");
        Assert.Equal("invalid_configuration", Assert.Throws<ToolFailureException>(() => fixture.Options.GetOptions("test")).Code);
        Assert.Equal("invalid_configuration", Assert.Throws<ToolFailureException>(() => fixture.Options.Resolve("test", [])).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingReferenceTransformRequiresRemovingAllItsStoredOptions(bool disable)
    {
        using var fixture = new Fixture(WorkflowPreparationTests.ReferenceGraph);
        var transforms = JsonNode.Parse("""[{"id":"refs","kind":"minimax_references","node_id":"136"}]""")!.AsArray();
        fixture.Configuration.SaveWorkflow("test", "image", null,
            new JsonObject { ["prompt"] = "hello", ["reference_images"] = new JsonArray("a.png") },
            new JsonObject { ["reference_audios"] = new JsonArray(new JsonArray("a.wav")) },
            JsonNode.Parse("""{"reference_videos":{"required":false},"reference_audios":{"required":true}}""")!.AsObject(), transforms: transforms);
        var removed = transforms.DeepClone().AsArray();
        if (disable) removed[0]!["enabled"] = false;
        else removed.Clear();
        var before = File.ReadAllText(fixture.YamlPath);
        var error = Assert.Throws<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, null,
            null, null, null, transforms: removed));
        Assert.Equal("unknown_parameter", error.Code);
        Assert.Equal(before, File.ReadAllText(fixture.YamlPath));
        fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["reference_images"] = null }, new JsonObject { ["reference_audios"] = null },
            new JsonObject { ["reference_videos"] = null, ["reference_audios"] = null }, transforms: removed);
        Assert.False(fixture.Configuration.Load("test").Defaults.ContainsKey("reference_images"));
        Assert.False(fixture.Options.Resolve("test", []).Values.ContainsKey("reference_images"));
    }

    [Fact]
    public void AutomaticSeedsRespectBoundsAndAllowedValues()
    {
        using var fixture = new Fixture(Graph);
        fixture.Configuration.SaveWorkflow("test", null, null,
            JsonNode.Parse("""{"prompt":"cat","steps":10,"cfg":7,"enabled":false}""")!.AsObject(), null,
            JsonNode.Parse("""{"seed":{"minimum":42,"maximum":42,"required":false}}""")!.AsObject());
        Assert.Equal(42, fixture.Options.Resolve("test", []).Values["seed"]!.GetValue<long>());
        fixture.Configuration.SaveWorkflow("test", null, null, null,
            new JsonObject { ["seed"] = new JsonArray(43) },
            JsonNode.Parse("""{"seed":{"maximum":44}}""")!.AsObject());
        Assert.Equal(43, fixture.Options.Resolve("test", []).Values["seed"]!.GetValue<long>());
        Assert.Throws<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, null, null,
            new JsonObject { ["seed"] = new JsonArray(-1) },
            JsonNode.Parse("""{"seed":{"minimum":null,"maximum":null}}""")!.AsObject()));
        fixture.Configuration.SaveWorkflow("test", null, null, new JsonObject { ["seed"] = -1 },
            new JsonObject { ["seed"] = new JsonArray(-1) },
            JsonNode.Parse("""{"seed":{"minimum":null,"maximum":null}}""")!.AsObject());
        Assert.Equal(-1, fixture.Options.Resolve("test", []).Values["seed"]!.GetValue<long>());
    }

    [Fact]
    public void RequiredMediaListDoesNotAdvertiseAnUnavailableEmptyDefault()
    {
        using var fixture = new Fixture(WorkflowPreparationTests.ReferenceGraph);
        var transforms = JsonNode.Parse("""[{"id":"refs","kind":"minimax_references","node_id":"136"}]""")!.AsArray();
        var allowed = new JsonObject { ["reference_images"] = new JsonArray(new JsonArray("a.png")) };
        Assert.Throws<ToolFailureException>(() => fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["prompt"] = "hello" }, allowed, null, transforms: transforms));
        Assert.False(File.Exists(fixture.YamlPath));
        fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["prompt"] = "hello" }, allowed,
            JsonNode.Parse("""{"reference_images":{"required":true}}""")!.AsObject(), transforms: transforms);
        var options = JsonSerializer.SerializeToNode(fixture.Options.GetOptions("test"))!;
        Assert.True(options["parameters"]!["reference_images"]!["required"]!.GetValue<bool>());
        Assert.Null(options["parameters"]!["reference_images"]!["default"]);
        Assert.Equal("reference_images", Assert.Throws<GenerationParameterException>(() => fixture.Options.Resolve("test", [])).Parameter);
        fixture.Configuration.SaveWorkflow("test", null, null,
            new JsonObject { ["reference_images"] = new JsonArray("a.png") }, null, null);
        options = JsonSerializer.SerializeToNode(fixture.Options.GetOptions("test"))!;
        Assert.False(options["parameters"]!["reference_images"]!["required"]!.GetValue<bool>());
        Assert.Equal("a.png", fixture.Options.Resolve("test", []).Values["reference_images"]![0]!.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        internal string JsonPath { get; }
        internal string YamlPath { get; }
        internal WorkflowConfigurationManager Configuration { get; }
        internal GenerationOptionsService Options { get; }

        internal Fixture(string graph)
        {
            var directory = Path.Combine(_temp.Path, "workflows");
            Directory.CreateDirectory(directory);
            JsonPath = Path.Combine(directory, "test.json");
            YamlPath = Path.Combine(directory, "test.yaml");
            File.WriteAllText(JsonPath, graph);
            var configuration = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", _temp.Path));
            Configuration = new WorkflowConfigurationManager(configuration);
            Options = new GenerationOptionsService(new WorkflowManager(configuration, Configuration), Configuration);
        }

        public void Dispose() => _temp.Dispose();
    }
}
