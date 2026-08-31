using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComfyUi.Mcp.Tests;

public sealed class ComfyMcpApplicationFactory : WebApplicationFactory<Program>
{
    public ComfyMcpApplicationFactory() : this("http://comfyui.test:8188")
    {
    }

    internal ComfyMcpApplicationFactory(string comfyUiBaseUrl) =>
        ComfyUiBaseUrl = comfyUiBaseUrl;

    public string ComfyUiBaseUrl { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ComfyMcp:BaseUrl", ComfyUiBaseUrl);
        builder.UseSetting("ComfyMcp:WorkflowDirectory", FindWorkflowDirectory());
        builder.UseSetting(
            "ComfyMcp:ConfigurationDirectory",
            Path.Combine(FindWorkflowDirectory(), "..", "configuration"));
    }

    private static string FindWorkflowDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "workflows");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.json").Any())
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository workflows directory.");
    }

}
