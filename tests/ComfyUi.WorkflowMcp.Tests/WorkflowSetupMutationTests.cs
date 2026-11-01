using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowSetupMutationTests
{
    [Fact]
    public void ConfigurationUpdatesMergeDefaultsListsAndConstraintRules()
    {
        using var temp = new TempDirectory();
        var (configuration, _) = CreateWorkflow(temp.Path);
        configuration.SaveWorkflow("image", "image", true,
            JsonNode.Parse("""{"prompt":"cat","aspect_ratio":"16:9"}""")!.AsObject(),
            JsonNode.Parse("""{"aspect_ratio":["16:9","1:1"]}""")!.AsObject(),
            JsonNode.Parse("""{"cfg":{"minimum":1,"maximum":20},"steps":{"minimum":1}}""")!.AsObject(), "Example");

        configuration.SaveWorkflow("image", null, null,
            new JsonObject { ["cfg"] = 7.5 },
            new JsonObject { ["cfg"] = new JsonArray(7.5, 8) },
            JsonNode.Parse("""{"cfg":{"maximum":15}}""")!.AsObject());
        configuration.SaveWorkflow("image", null, null, [], [], []);

        var saved = configuration.Load("image");
        Assert.Equal("cat", saved.Defaults["prompt"]!.ToString());
        Assert.Equal("16:9", saved.Defaults["aspect_ratio"]!.ToString());
        Assert.Equal("7.5", saved.Defaults["cfg"]!.ToString());
        Assert.Equal(2, saved.AllowedValues["aspect_ratio"]!.AsArray().Count);
        Assert.Equal("1", saved.Constraints["cfg"]!["minimum"]!.ToString());
        Assert.Equal("15", saved.Constraints["cfg"]!["maximum"]!.ToString());
        Assert.Equal("1", saved.Constraints["steps"]!["minimum"]!.ToString());
        Assert.Equal("Example", saved.Description);
        Assert.True(saved.WaitForCompletion);

        configuration.SaveWorkflow("image", null, null,
            new JsonObject { ["cfg"] = 8 },
            new JsonObject { ["cfg"] = new JsonArray(8) }, null);
        Assert.Equal("8", configuration.Load("image").Defaults["cfg"]!.ToString());
        Assert.Single(configuration.Load("image").AllowedValues["cfg"]!.AsArray());
    }

    [Fact]
    public void NullEntriesRemoveOnlySelectedConfigurationAndKeepBindings()
    {
        using var temp = new TempDirectory();
        var (configuration, workflows) = CreateWorkflow(temp.Path);
        configuration.SaveWorkflow("image", "image", null,
            new JsonObject { ["prompt"] = "cat", ["cfg"] = 7 },
            JsonNode.Parse("""{"cfg":[7,8],"steps":[10,20]}""")!.AsObject(),
            JsonNode.Parse("""{"cfg":{"minimum":1,"maximum":20},"steps":{"minimum":1}}""")!.AsObject());
        configuration.SaveWorkflow("image", null, null,
            new JsonObject { ["cfg"] = null }, new JsonObject { ["cfg"] = null },
            JsonNode.Parse("""{"cfg":{"maximum":null},"steps":null}""")!.AsObject());

        var saved = configuration.Load("image");
        Assert.False(saved.Defaults.ContainsKey("cfg"));
        Assert.Equal("cat", saved.Defaults["prompt"]!.ToString());
        Assert.False(saved.AllowedValues.ContainsKey("cfg"));
        Assert.True(saved.AllowedValues.ContainsKey("steps"));
        Assert.False(saved.Constraints["cfg"]!.AsObject().ContainsKey("maximum"));
        Assert.Equal("1", saved.Constraints["cfg"]!["minimum"]!.ToString());
        Assert.False(saved.Constraints.ContainsKey("steps"));
        Assert.Contains("cfg", workflows.ExtractParameters(workflows.Load("image")!).Keys);
    }

    [Fact]
    public void UnparameterizeRestoresTypedDefaultsAtEveryBindingAndRemovesMetadata()
    {
        using var temp = new TempDirectory();
        var (configuration, workflows) = CreateWorkflow(temp.Path);
        configuration.SaveWorkflow("image", "image", null,
            JsonNode.Parse("""{"prompt":"cat","aspect_ratio":"16:9","cfg":7.5,"cfg_scale":2,"steps":10,"enabled":false}""")!.AsObject(),
            new JsonObject { ["cfg"] = new JsonArray(7.5, 8) },
            JsonNode.Parse("""{"cfg":{"minimum":1,"maximum":20}}""")!.AsObject());

        configuration.UnparameterizeWorkflow("image", ["cfg", "steps", "enabled"], workflows);

        var workflow = workflows.Load("image")!;
        Assert.Equal(7.5, workflow["2"]!["inputs"]!["cfg"]!.GetValue<double>());
        Assert.Equal(7.5, workflow["3"]!["inputs"]!["cfg"]!.GetValue<double>());
        Assert.Equal(10, workflow["2"]!["inputs"]!["steps"]!.GetValue<int>());
        Assert.False(workflow["2"]!["inputs"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("CFG 7.5 / PARAM_FLOAT_CFG_SCALE", workflow["4"]!["inputs"]!["text"]!.ToString());
        var saved = configuration.Load("image");
        foreach (var name in new[] { "cfg", "steps", "enabled" })
        {
            Assert.False(saved.Defaults.ContainsKey(name));
            Assert.False(saved.AllowedValues.ContainsKey(name));
            Assert.False(saved.Constraints.ContainsKey(name));
            Assert.DoesNotContain(name, workflows.ExtractParameters(workflow).Keys);
        }
        // Remaining defaults must still resolve without orphaned removed parameters.
        var resolved = new GenerationOptionsService(workflows, configuration).Resolve("image", []);
        Assert.Equal("cat", resolved.Values["prompt"]!.ToString());
        Assert.Equal("16:9", resolved.Values["aspect_ratio"]!.ToString());
        Assert.Equal("2", resolved.Values["cfg_scale"]!.ToString());
    }

    [Theory]
    [InlineData("unknown", "10")]
    [InlineData("steps", null)]
    [InlineData("steps", "\"invalid\"")]
    [InlineData("steps", "10.5")]
    [InlineData("steps", "[10]")]
    [InlineData("steps", "50")]
    [InlineData("prompt", "\"PARAM_PROMPT\"")]
    public void InvalidBatchRemovalLeavesBothFilesUnchanged(string secondName, string? defaultJson)
    {
        using var temp = new TempDirectory();
        var (configuration, workflows) = CreateWorkflow(temp.Path);
        var defaults = new JsonObject { ["cfg"] = 7.5 };
        if (defaultJson is not null) defaults[secondName] = JsonNode.Parse(defaultJson);
        var jsonPath = Path.Combine(temp.Path, "workflows", "image.json");
        var yamlPath = Path.ChangeExtension(jsonPath, ".yaml");
        // Simulate an invalid externally edited YAML file; configuration tools now reject these values.
        File.WriteAllText(yamlPath, new JsonObject
        {
            ["defaults"] = defaults,
            ["constraints"] = JsonNode.Parse("""{"steps":{"maximum":20}}""")
        }.ToJsonString());
        var json = File.ReadAllText(jsonPath);
        var yaml = File.ReadAllText(yamlPath);

        Assert.ThrowsAny<ArgumentException>(() => configuration.UnparameterizeWorkflow("image", ["cfg", secondName], workflows));

        Assert.Equal(json, File.ReadAllText(jsonPath));
        Assert.Equal(yaml, File.ReadAllText(yamlPath));
    }

    [Fact]
    public void IncrementalUpdatesPreserveLegacyModelDefault()
    {
        using var temp = new TempDirectory();
        var (configuration, workflows) = CreateWorkflow(temp.Path);
        var workflow = workflows.Load("image")!;
        workflow["5"] = JsonNode.Parse("""{"class_type":"Loader","inputs":{"model":"PARAM_MODEL"}}""");
        File.WriteAllText(Path.Combine(temp.Path, "workflows", "image.json"), workflow.ToJsonString());
        File.WriteAllText(Path.Combine(temp.Path, "workflows", "image.yaml"), "model: example.safetensors\n");

        configuration.SaveWorkflow("image", null, null, new JsonObject { ["cfg"] = 7 }, null, null);

        Assert.Equal("example.safetensors", configuration.Load("image").Defaults["model"]!.ToString());
    }

    private static (WorkflowConfigurationManager Configuration, WorkflowManager Workflows) CreateWorkflow(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "workflows"));
        File.WriteAllText(Path.Combine(root, "workflows", "image.json"),
            """
            {
              "1":{"class_type":"Text","inputs":{"prompt":"PARAM_PROMPT","aspect_ratio":"PARAM_ASPECT_RATIO"}},
              "2":{"class_type":"Sampler","inputs":{"cfg":"PARAM_FLOAT_CFG","cfg_scale":"PARAM_FLOAT_CFG_SCALE","steps":"PARAM_INT_STEPS","enabled":"PARAM_BOOL_ENABLED"}},
              "3":{"class_type":"Sampler","inputs":{"cfg":"PARAM_FLOAT_CFG"}},
              "4":{"class_type":"Text","inputs":{"text":"CFG PARAM_FLOAT_CFG / PARAM_FLOAT_CFG_SCALE"}}
            }
            """);
        var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", root));
        var configuration = new WorkflowConfigurationManager(config);
        return (configuration, new WorkflowManager(config, configuration));
    }
}
