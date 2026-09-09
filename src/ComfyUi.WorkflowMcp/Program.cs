using ComfyUi.WorkflowMcp;
using Microsoft.Extensions.Http.Resilience;

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
    .WithRequestFilters(filters => filters.AddListToolsFilter(next =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = [.. result.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)];
            return result;
        }));
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
