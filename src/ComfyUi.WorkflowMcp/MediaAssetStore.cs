using System.Text.RegularExpressions;

namespace ComfyUi.WorkflowMcp;

public sealed partial class MediaAssetStore(IConfiguration configuration)
{
    private const int MaximumDecodedBytes = 100 * 1024 * 1024;

    public async Task<StoredMediaAsset> StageAsync(
        string data,
        string? filename,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        var (encoded, dataUrlMimeType) = ParseData(data);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new ArgumentException("data must be a base64 string or a base64 data URL.", nameof(data), ex); }
        if (bytes.Length > MaximumDecodedBytes)
            throw new ArgumentException("Decoded media exceeds the 100 MB limit.", nameof(data));

        var resolvedMimeType = mimeType ?? dataUrlMimeType ?? "application/octet-stream";
        var resolvedFilename = CreateFilename(filename, resolvedMimeType);
        var assetsDirectory = configuration.AssetsRoot();
        Directory.CreateDirectory(assetsDirectory);
        var path = Path.Combine(assetsDirectory, resolvedFilename);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return new StoredMediaAsset(path, resolvedFilename, resolvedMimeType, bytes.Length);
    }

    public IReadOnlyList<StoredMediaAsset> ListAssets()
    {
        var root = configuration.AssetsRoot();
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new StoredMediaAsset(path, Path.GetFileName(path), ComfyClient.MimeFor(path), new FileInfo(path).Length))
            .ToArray();
    }

    public string? Find(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename)
            throw new ArgumentException("filename must be a single file name without directory components.", nameof(filename));
        var path = Path.Combine(configuration.AssetsRoot(), filename);
        return File.Exists(path) ? path : null;
    }

    private static (string Encoded, string? MimeType) ParseData(string data)
    {
        const string marker = ";base64,";
        if (!data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return (data, null);
        var markerIndex = data.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 5) throw new ArgumentException("data URL must use base64 encoding.", nameof(data));
        return (data[(markerIndex + marker.Length)..], data[5..markerIndex]);
    }

    private static string CreateFilename(string? filename, string mimeType)
    {
        var requested = Path.GetFileName(filename ?? "");
        if (string.IsNullOrWhiteSpace(requested)) requested = $"asset-{Guid.NewGuid():N}";
        var stem = UnsafeFilenameCharacters().Replace(Path.GetFileNameWithoutExtension(requested), "_").Trim('_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "asset";
        if (stem.Length > 80) stem = stem[..80];
        var extension = Path.GetExtension(requested);
        if (string.IsNullOrWhiteSpace(extension)) extension = Extension(mimeType);
        return $"{stem}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
    }

    private static string Extension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/png" => ".png",
        "audio/wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "audio/flac" => ".flac",
        "audio/ogg" => ".ogg",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        _ => ".bin"
    };

    [GeneratedRegex("[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeFilenameCharacters();
}

public sealed record StoredMediaAsset(string Path, string Filename, string MimeType, long Bytes);
