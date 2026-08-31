using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class ComfyWorkflowMcpApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _fixtureDirectory;

    public ComfyWorkflowMcpApplicationFactory() : this("http://comfyui.test:8188")
    {
    }

    internal ComfyWorkflowMcpApplicationFactory(string comfyUiBaseUrl)
    {
        ComfyUiBaseUrl = comfyUiBaseUrl;
        _fixtureDirectory = Path.Combine(
            Path.GetTempPath(), $"comfyui-workflow-mcp-tests-{Guid.NewGuid():N}");
        var workflowDirectory = Path.Combine(_fixtureDirectory, "workflow-json");
        var configurationDirectory = Path.Combine(
            _fixtureDirectory, "workflow-yaml", "workflows");
        Directory.CreateDirectory(workflowDirectory);
        Directory.CreateDirectory(configurationDirectory);
        File.WriteAllText(
            Path.Combine(workflowDirectory, "test_image.json"),
            """
            {
              "1": {
                "inputs": { "prompt": "PARAM_PROMPT" },
                "class_type": "SaveImage"
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(configurationDirectory, "test_image.yaml"),
            "media_type: image\n");
    }

    public string ComfyUiBaseUrl { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ComfyWorkflowMcp:BaseUrl", ComfyUiBaseUrl);
        builder.UseSetting(
            "ComfyWorkflowMcp:WorkflowDirectory",
            Path.Combine(_fixtureDirectory, "workflow-json"));
        builder.UseSetting(
            "ComfyWorkflowMcp:ConfigurationDirectory",
            Path.Combine(_fixtureDirectory, "workflow-yaml"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_fixtureDirectory))
            Directory.Delete(_fixtureDirectory, recursive: true);
    }
}
