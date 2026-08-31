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
        var workflowDirectory = Path.Combine(_fixtureDirectory, "workflows");
        Directory.CreateDirectory(workflowDirectory);
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
            Path.Combine(workflowDirectory, "test_image.yaml"),
            "media_type: image\ndefaults:\n  prompt: hello\nallowed_values:\n  prompt: [hello, goodbye]\n");
    }

    public string ComfyUiBaseUrl { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ComfyWorkflowMcp:BaseUrl", ComfyUiBaseUrl);
        builder.UseSetting("ComfyWorkflowMcp:Directory", _fixtureDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_fixtureDirectory))
            Directory.Delete(_fixtureDirectory, recursive: true);
    }
}
