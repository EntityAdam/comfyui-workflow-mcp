using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class GeneratedMediaStoreTests
{
    [Fact]
    public async Task SavesCompletedMediaUnderConfiguredOutputDirectory()
    {
        using var temp = new TempDirectory();
        var store = new GeneratedMediaStore(TestConfiguration.Create(
            ("ComfyWorkflowMcp:Directory", temp.Path)));
        byte[] bytes = [1, 2, 3, 4];
        var completed = new CompletedWorkflow(
            "prompt-1",
            new ComfyOutputAsset("unsafe image.png", "outputs", "output"),
            bytes,
            "image/png");

        var path = await store.SaveAsync(completed, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(temp.Path, "output"), Path.GetDirectoryName(path));
        Assert.Matches("^unsafe_image-[a-f0-9]{32}\\.png$", Path.GetFileName(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SavesCompletedAudioWithMatchingExtension()
    {
        using var temp = new TempDirectory();
        var store = new GeneratedMediaStore(TestConfiguration.Create(
            ("ComfyWorkflowMcp:Directory", temp.Path)));
        var completed = new CompletedWorkflow(
            "prompt-1",
            new ComfyOutputAsset("audio.mp3", "outputs", "output"),
            [1],
            "audio/mpeg");

        var path = await store.SaveAsync(completed, TestContext.Current.CancellationToken);

        Assert.EndsWith(".mp3", path);
        Assert.Equal([1], await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }
}
