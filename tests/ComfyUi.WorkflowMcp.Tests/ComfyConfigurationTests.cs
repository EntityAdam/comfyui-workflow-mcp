using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class ComfyConfigurationTests
{
    [Fact]
    public void InputRootDefaultsToDedicatedDirectoryBesideExecutable()
    {
        var configuration = TestConfiguration.Create();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "input"),
            configuration.InputRoot());
    }

    [Fact]
    public void InputRootCanBeConfiguredExplicitly()
    {
        using var temp = new TempDirectory();
        var configuration = TestConfiguration.Create(
            ("ComfyWorkflowMcp:InputRoot", temp.Path));

        Assert.Equal(temp.Path, configuration.InputRoot());
    }
}
