using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Edit a reference image while preserving its composition.")]
    [InlineData("Generate an image.\nUse for detailed illustrations.\n")]
    public void WorkflowDescriptionsAreOptionalAndSurviveOtherConfigurationChanges(string? description)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "workflows");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "image.json"),
            """{"1":{"class_type":"Text","inputs":{"text":"PARAM_PROMPT"}}}""");
        var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path));
        var configuration = new WorkflowConfigurationManager(config);
        var workflows = new WorkflowManager(config, configuration);
        Assert.Null(configuration.Load("image").Description);
        Assert.Equal("Execute the 'image' workflow.", Assert.Single(workflows.Catalog()).Description);

        configuration.SaveWorkflow("image", "image", null, null, null, null, description);
        configuration.SaveWorkflow("image", null, false, null, null, null);

        var expected = string.IsNullOrWhiteSpace(description) ? null : description;
        Assert.Equal(expected, configuration.Load("image").Description);
        Assert.Equal(expected ?? "Execute the 'image' workflow.", Assert.Single(workflows.Catalog()).Description);

        configuration.SaveWorkflow("image", null, null, null, null, null, "");
        Assert.Null(configuration.Load("image").Description);
        Assert.Equal("Execute the 'image' workflow.", Assert.Single(workflows.Catalog()).Description);
    }

    [Fact]
    public void WorkflowYamlDefaultsAndConstraintsPreventInvalidRuns()
    {
        using var temp = new TempDirectory();
        var workflowDirectory = Path.Combine(temp.Path, "workflows");
        Directory.CreateDirectory(workflowDirectory);
        File.WriteAllText(Path.Combine(workflowDirectory, "image.json"),
            """{"1":{"class_type":"Sampler","inputs":{"prompt":"PARAM_PROMPT","steps":"PARAM_INT_STEPS","sampler":"PARAM_SAMPLER"}}}""");
        File.WriteAllText(Path.Combine(workflowDirectory, "image.yaml"),
            """
            media_type: image
            wait_for_completion: false
            defaults:
              steps: 8
              sampler: euler
            allowed_values:
              sampler: [euler, dpmpp_2m]
            constraints:
              steps:
                minimum: 1
                maximum: 20
            """);
        var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path));
        var manager = new WorkflowManager(config, new WorkflowConfigurationManager(config));
        var options = new GenerationOptionsService(manager, new WorkflowConfigurationManager(config));

        Assert.False(new WorkflowConfigurationManager(config).Load("image").WaitForCompletion);

        var resolved = options.Resolve("image", new JsonObject { ["prompt"] = "cat" });
        Assert.Equal("8", resolved.Values["steps"]!.ToString());
        Assert.Throws<GenerationParameterException>(() => options.Resolve(
            "image", new JsonObject { ["prompt"] = "cat", ["steps"] = 50 }));
        Assert.Throws<GenerationParameterException>(() => options.Resolve(
            "image", new JsonObject { ["prompt"] = "cat", ["sampler"] = "invalid" }));
    }

    [Fact]
    public void SetupParameterizesCopiedWorkflowInPlace()
    {
        using var temp = new TempDirectory();
        var workflowDirectory = Path.Combine(temp.Path, "workflows");
        Directory.CreateDirectory(workflowDirectory);
        File.WriteAllText(Path.Combine(workflowDirectory, "copied.json"),
            """{"7":{"class_type":"KSampler","inputs":{"steps":20}}}""");
        var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path));
        var workflows = new WorkflowManager(config, new WorkflowConfigurationManager(config));
        var manager = new WorkflowConfigurationManager(config);

        manager.ParameterizeWorkflow("copied", new JsonObject
        {
            ["steps"] = new JsonObject { ["node_id"] = "7", ["input_name"] = "steps", ["type"] = "integer" }
        }, workflows);

        Assert.Equal("PARAM_INT_STEPS", workflows.Load("copied")!["7"]!["inputs"]!["steps"]!.ToString());
    }

    [Fact]
    public void ParameterizationCallsAreAdditive()
    {
        using var temp = new TempDirectory();
        var workflowDirectory = Path.Combine(temp.Path, "workflows");
        Directory.CreateDirectory(workflowDirectory);
        File.WriteAllText(Path.Combine(workflowDirectory, "copied.json"),
            """{"6":{"class_type":"Text","inputs":{"text":""}},"7":{"class_type":"KSampler","inputs":{"steps":20}}}""");
        var config = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path));
        var workflows = new WorkflowManager(config, new WorkflowConfigurationManager(config));
        var manager = new WorkflowConfigurationManager(config);

        manager.ParameterizeWorkflow("copied", new JsonObject
        {
            ["prompt"] = new JsonObject { ["node_id"] = "6", ["input_name"] = "text", ["type"] = "string" }
        }, workflows);
        manager.ParameterizeWorkflow("copied", new JsonObject
        {
            ["steps"] = new JsonObject { ["node_id"] = "7", ["input_name"] = "steps", ["type"] = "integer" }
        }, workflows);

        var result = workflows.Load("copied")!;
        Assert.Equal("PARAM_STR_PROMPT", result["6"]!["inputs"]!["text"]!.ToString());
        Assert.Equal("PARAM_INT_STEPS", result["7"]!["inputs"]!["steps"]!.ToString());
    }
}
