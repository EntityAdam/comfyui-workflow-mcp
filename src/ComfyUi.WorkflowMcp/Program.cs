using ComfyUi.WorkflowMcp;
using Microsoft.Extensions.Http.Resilience;
using ModelContextProtocol.Protocol;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WorkflowManager>();
builder.Services.AddSingleton<WorkflowConfigurationManager>();
builder.Services.AddSingleton<GenerationOptionsService>();
builder.Services.AddSingleton<WorkflowExecutionService>();
builder.Services.AddSingleton<GeneratedMediaStore>();
builder.Services.AddSingleton<MediaAssetStore>();
builder.Services
    .AddHttpClient<ComfyClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(30);
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(10);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(30);
        options.Retry.DisableForUnsafeHttpMethods();
    });

var stdio = args.Contains("--stdio", StringComparer.Ordinal);
var mcp = builder.Services
    .AddMcpServer()
    .WithTools<ComfyTools>()
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = [.. result.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)];
            return result;
        });
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            const string recovery = "Use the required fields and types shown in this tool's input schema.";
            try
            {
                var result = await next(context, cancellationToken);
                // Object-returning tools may be encoded as JSON text by the SDK.
                if (result.StructuredContent is null && result.Content.FirstOrDefault() is TextContentBlock text && text.Text.StartsWith('{'))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<JsonElement>(text.Text);
                        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("success", out var failed) && failed.ValueKind == JsonValueKind.False)
                            result.StructuredContent = body;
                    }
                    catch (JsonException) { /* Non-JSON text remains ordinary tool content. */ }
                }
                if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured &&
                    structured.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                    result.IsError = true;
                // The SDK may report binding failures before invoking a tool method.
                if (result.IsError == true && result.StructuredContent is null)
                    return ToolErrors.Result(new ToolFailureException("invalid_arguments",
                        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)), recovery), recovery);
                return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return ToolErrors.Result(new ToolFailureException("invalid_arguments", ex.Message, recovery), recovery);
            }
        });
    });
if (stdio) mcp.WithStdioServerTransport();
else mcp.WithHttpTransport(options => options.Stateless = true);

var app = builder.Build();
app.MapGet("/health", (IConfiguration configuration) =>
    Results.Ok(new { status = "ok", comfyui = configuration.BaseUrl() }));
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    app.Urls.Add(builder.Configuration.ServerUrl());
if (stdio) await app.RunAsync();
else
{
    app.MapMcp("/mcp");
    await app.RunAsync();
}

public partial class Program;
