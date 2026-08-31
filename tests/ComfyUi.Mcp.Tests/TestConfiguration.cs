using Microsoft.Extensions.Configuration;

namespace ComfyUi.Mcp.Tests;

internal static class TestConfiguration
{
    public static IConfiguration Create(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => item.Value))
            .Build();
}
