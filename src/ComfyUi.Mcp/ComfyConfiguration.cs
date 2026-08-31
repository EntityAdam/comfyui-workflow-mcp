using Microsoft.Extensions.Configuration;

namespace ComfyUi.Mcp;

public static class ComfyConfiguration
{
    public const string SectionName = "ComfyMcp";
    public const string BaseUrlKey = $"{SectionName}:BaseUrl";
    public const string ServerUrlKey = $"{SectionName}:ServerUrl";
    public const string WorkflowDirectoryKey = $"{SectionName}:WorkflowDirectory";
    public const string ConfigurationDirectoryKey = $"{SectionName}:ConfigurationDirectory";
    public const string InputRootKey = $"{SectionName}:InputRoot";

    public static string BaseUrl(this IConfiguration configuration) => configuration[BaseUrlKey] ?? "http://localhost:8188";
    public static string ServerUrl(this IConfiguration configuration) => configuration[ServerUrlKey] ?? "http://localhost:9001";
    public static string ConfigurationDirectory(this IConfiguration configuration) =>
        Path.GetFullPath(configuration[ConfigurationDirectoryKey] ?? Path.Combine(AppContext.BaseDirectory, "configuration"));

    public static string WorkflowDirectory(this IConfiguration configuration)
    {
        var settings = Path.Combine(configuration.ConfigurationDirectory(), "settings.yaml");
        if (File.Exists(settings))
        {
            var line = File.ReadLines(settings).FirstOrDefault(value =>
                value.TrimStart().StartsWith("workflow_directory:", StringComparison.Ordinal));
            var value = line?[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(value);
        }
        return Path.GetFullPath(configuration[WorkflowDirectoryKey] ?? Path.Combine(AppContext.BaseDirectory, "workflows"));
    }

    public static string InputRoot(this IConfiguration configuration) =>
        Path.GetFullPath(configuration[InputRootKey] ?? Directory.GetCurrentDirectory());
}
