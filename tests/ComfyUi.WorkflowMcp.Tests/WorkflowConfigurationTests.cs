using System.Text.Json.Nodes;
using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class WorkflowConfigurationTests
{
    [Fact]
    public void WorkflowYamlDefaultsAndConstraintsPreventInvalidRuns()
    {
        using var temp = new TempDirectory();
        var workflowDirectory = Path.Combine(temp.Path, "workflows");
        var configurationDirectory = Path.Combine(temp.Path, "configuration");
        Directory.CreateDirectory(workflowDirectory);
        Directory.CreateDirectory(Path.Combine(configurationDirectory, "workflows"));
        File.WriteAllText(Path.Combine(workflowDirectory, "image.json"),
            """{"1":{"class_type":"Sampler","inputs":{"prompt":"PARAM_PROMPT","steps":"PARAM_INT_STEPS","sampler":"PARAM_SAMPLER"}}}""");
        File.WriteAllText(Path.Combine(configurationDirectory, "workflows", "image.yaml"),
            """
            media_type: image
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
        var config = TestConfiguration.Create(
            ("ComfyWorkflowMcp:WorkflowDirectory", workflowDirectory),
            ("ComfyWorkflowMcp:ConfigurationDirectory", configurationDirectory));
        var manager = new WorkflowManager(config);
        var options = new GenerationOptionsService(manager, new WorkflowConfigurationManager(config));

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
        var config = TestConfiguration.Create(
            ("ComfyWorkflowMcp:WorkflowDirectory", workflowDirectory),
            ("ComfyWorkflowMcp:ConfigurationDirectory", Path.Combine(temp.Path, "configuration")));
        var workflows = new WorkflowManager(config);
        var manager = new WorkflowConfigurationManager(config);

        manager.ParameterizeWorkflow("copied", new JsonObject
        {
            ["steps"] = new JsonObject { ["node_id"] = "7", ["input_name"] = "steps", ["type"] = "integer" }
        }, workflows);

        Assert.Equal("PARAM_INT_STEPS", workflows.Load("copied")!["7"]!["inputs"]!["steps"]!.ToString());
    }
}
