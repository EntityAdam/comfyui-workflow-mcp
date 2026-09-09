using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class MediaAssetStoreTests
{
    [Fact]
    public async Task StageSavesDataUrlUnderAssetsDirectory()
    {
        using var temp = new TempDirectory();
        var store = new MediaAssetStore(TestConfiguration.Create(
            ("ComfyWorkflowMcp:Directory", temp.Path)));

        var asset = await store.StageAsync(
            "data:image/png;base64,AQIDBA==",
            "reference image.png",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(temp.Path, "assets"), Path.GetDirectoryName(asset.Path));
        Assert.Matches("^reference_image-[a-f0-9]{32}\\.png$", asset.Filename);
        Assert.Equal("image/png", asset.MimeType);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(asset.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAndFindUseManagedAssets()
    {
        using var temp = new TempDirectory();
        var configuration = TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path));
        var store = new MediaAssetStore(configuration);
        var asset = await store.StageAsync("AQI=", "asset.bin", "application/octet-stream", TestContext.Current.CancellationToken);
        var assets = store.ListAssets();

        Assert.Single(assets);
        Assert.Equal(asset.Path, store.Find(asset.Filename));
        Assert.Null(store.Find("manual.png"));
        Assert.Throws<ArgumentException>(() => store.Find("../manual.png"));
    }
}
