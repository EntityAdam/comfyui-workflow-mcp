using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ComfyUi.Mcp;

public sealed class ComfyClient(HttpClient httpClient, IConfiguration configuration)
{
    public string BaseUrl => configuration.BaseUrl().TrimEnd('/');

    public async Task<ComfySubmissionResult> SubmitAsync(JsonObject workflow, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["prompt"] = workflow.DeepClone() };
        if (!string.IsNullOrWhiteSpace(clientId)) payload["client_id"] = clientId;
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/prompt", payload, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ComfyUI rejected workflow ({(int)response.StatusCode}): {body}");
        if (body?["node_errors"] is JsonObject nodeErrors && nodeErrors.Count > 0)
            throw new InvalidOperationException($"ComfyUI rejected one or more output branches: {nodeErrors}");
        var promptId = body?["prompt_id"]?.ToString() ?? throw new InvalidOperationException($"ComfyUI did not return prompt_id: {body}");
        return new ComfySubmissionResult(promptId, body["number"]?.GetValue<int>(), workflow.DeepClone().AsObject());
    }

    public async Task<JsonObject> GetQueueAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<JsonObject>($"{BaseUrl}/queue", cancellationToken) ?? [];
    public async Task<JsonObject> GetHistoryAsync(string promptId, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<JsonObject>($"{BaseUrl}/history/{Uri.EscapeDataString(promptId)}", cancellationToken) ?? [];

    public async Task<byte[]> FetchOutputAsync(ComfyOutputAsset output, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/view?filename={Uri.EscapeDataString(output.Filename)}&subfolder={Uri.EscapeDataString(output.Subfolder)}&type={Uri.EscapeDataString(output.FolderType)}";
        return await httpClient.GetByteArrayAsync(url, cancellationToken);
    }

    public async Task<JsonObject> CancelAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var queue = await GetQueueAsync(cancellationToken);
        var running = QueueContainsPrompt(queue["queue_running"], promptId);
        var pending = QueueContainsPrompt(queue["queue_pending"], promptId);
        if (running)
        {
            using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/interrupt", new { }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        else if (pending)
        {
            using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/queue", new { delete = new[] { promptId } }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        return new JsonObject
        {
            ["success"] = running || pending,
            ["prompt_id"] = promptId,
            ["state"] = running ? "running_interrupted" : pending ? "pending_removed" : "not_found"
        };
    }

    public async Task ClearPendingAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/queue", new { clear = true }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ComfyInputUpload> UploadInputAsync(Stream content, string filename, string? contentType = null, string? subfolder = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(content);
        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType)) file.Headers.ContentType = mediaType;
        form.Add(file, "image", Path.GetFileName(filename));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");
        if (!string.IsNullOrWhiteSpace(subfolder)) form.Add(new StringContent(subfolder), "subfolder");
        using var response = await httpClient.PostAsync($"{BaseUrl}/upload/image", form, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ComfyUI rejected input upload ({(int)response.StatusCode}): {body}");
        return new ComfyInputUpload(
            body?["name"]?.ToString() ?? throw new InvalidOperationException($"ComfyUI upload did not return a name: {body}"),
            body["subfolder"]?.ToString() ?? "",
            body["type"]?.ToString() ?? "input");
    }

    public static bool QueueContainsPrompt(JsonNode? queue, string promptId) =>
        queue is JsonArray items && items.Any(item => item is JsonArray tuple && tuple.Any(value => string.Equals(value?.ToString(), promptId, StringComparison.Ordinal)));

    public static IReadOnlyList<ComfyOutputAsset> FindOutputs(JsonObject outputs)
    {
        var result = new List<ComfyOutputAsset>();
        foreach (var node in outputs.Select(pair => pair.Value).OfType<JsonObject>())
            foreach (var key in new[] { "images", "audio", "gifs", "videos" })
                if (node[key] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                        if (item["filename"] is { } filename)
                            result.Add(new(filename.ToString(), item["subfolder"]?.ToString() ?? "", item["type"]?.ToString() ?? "output"));
        return result.DistinctBy(item => (item.FolderType, item.Subfolder, item.Filename)).ToArray();
    }

    public static string MimeFor(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };
}
