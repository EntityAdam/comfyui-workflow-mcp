using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class ComfyConfigurationTests
{
    [Fact]
    public void PathsDefaultToDirectoriesBesideExecutable()
    {
        var configuration = TestConfiguration.Create();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "input"),
            configuration.InputRoot());
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "workflows"),
            configuration.WorkflowDirectory());
    }

    [Fact]
    public void DirectoryConfiguresSettingsWorkflowsAndInputTogether()
    {
        using var temp = new TempDirectory();
        var configuration = TestConfiguration.Create(
            ("ComfyWorkflowMcp:Directory", temp.Path));

        Assert.Equal(temp.Path, configuration.Directory());
        Assert.Equal(Path.Combine(temp.Path, "workflows"), configuration.WorkflowDirectory());
        Assert.Equal(Path.Combine(temp.Path, "input"), configuration.InputRoot());
        Assert.Equal(Path.Combine(temp.Path, "settings.yaml"),
            new WorkflowConfigurationManager(configuration).SettingsPath);
    }
}
