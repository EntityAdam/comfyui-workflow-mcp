using Xunit;

namespace ComfyUi.WorkflowMcp.Tests;

public sealed class MediaAssetStoreTests
{
    [Theory]
    [InlineData("reference.PNG", "image/png")]
    [InlineData("reference.jpeg", "image/jpeg")]
    [InlineData("reference.webp", "image/webp")]
    [InlineData("reference.gif", "image/gif")]
    public async Task ReadImageLoadsFilesPlacedDirectlyInAssets(string filename, string mimeType)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "assets");
        Directory.CreateDirectory(root);
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(root, filename), bytes, TestContext.Current.CancellationToken);
        var store = new MediaAssetStore(TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path)));

        var image = await store.ReadImageAsync(filename, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, image.Bytes);
        Assert.Equal(mimeType, image.MimeType);
    }

    [Theory]
    [InlineData("audio.wav", 4)]
    [InlineData("empty.png", 0)]
    [InlineData("large.png", 100 * 1024 * 1024 + 1)]
    public async Task ReadImageRejectsUnsupportedOrInvalidSizeAssets(string filename, long length)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "assets");
        Directory.CreateDirectory(root);
        using (var file = File.Create(Path.Combine(root, filename))) file.SetLength(length);
        var store = new MediaAssetStore(TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path)));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadImageAsync(filename, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadImageRejectsSymbolicLinksToOutsideAssets()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "assets");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(temp.Path, "private.png");
        await File.WriteAllBytesAsync(outside, [1], TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(root, "linked.png"), outside);
        var store = new MediaAssetStore(TestConfiguration.Create(("ComfyWorkflowMcp:Directory", temp.Path)));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadImageAsync("linked.png", TestContext.Current.CancellationToken));
    }

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
