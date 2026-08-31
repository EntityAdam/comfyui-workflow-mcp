using Microsoft.Extensions.Configuration;

namespace ComfyUi.WorkflowMcp;

public static class ComfyConfiguration
{
    public const string SectionName = "ComfyWorkflowMcp";
    public const string BaseUrlKey = $"{SectionName}:BaseUrl";
    public const string ServerUrlKey = $"{SectionName}:ServerUrl";
    public const string DirectoryKey = $"{SectionName}:Directory";

    public static string BaseUrl(this IConfiguration configuration) => configuration[BaseUrlKey] ?? "http://localhost:8188";
    public static string ServerUrl(this IConfiguration configuration) => configuration[ServerUrlKey] ?? "http://localhost:9001";
    public static string Directory(this IConfiguration configuration) =>
        Path.GetFullPath(configuration[DirectoryKey] ?? AppContext.BaseDirectory);

    public static string WorkflowDirectory(this IConfiguration configuration) =>
        Path.Combine(configuration.Directory(), "workflows");

    public static string InputRoot(this IConfiguration configuration) =>
        Path.Combine(configuration.Directory(), "input");
}
