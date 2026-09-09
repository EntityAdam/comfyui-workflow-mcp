using System.Text.RegularExpressions;

namespace ComfyUi.WorkflowMcp;

public sealed partial class GeneratedMediaStore(IConfiguration configuration)
{
    public async Task<string> SaveAsync(
        CompletedWorkflow completed,
        CancellationToken cancellationToken)
    {
        var outputDirectory = configuration.OutputRoot();
        Directory.CreateDirectory(outputDirectory);
        var originalStem = Path.GetFileNameWithoutExtension(completed.Output.Filename);
        var safeStem = UnsafeFilenameCharacters().Replace(originalStem, "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "generated";
        if (safeStem.Length > 80) safeStem = safeStem[..80];
        var filename = $"{safeStem}-{Guid.NewGuid():N}{Extension(completed.MimeType)}";
        var path = Path.Combine(outputDirectory, filename);

        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await destination.WriteAsync(completed.Bytes, cancellationToken);
        return path;
    }

    private static string Extension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/avif" => ".avif",
        "audio/wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "audio/flac" => ".flac",
        "audio/ogg" => ".ogg",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        _ => ".png"
    };

    [GeneratedRegex("[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeFilenameCharacters();
}
