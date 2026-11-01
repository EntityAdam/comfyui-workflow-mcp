using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class WorkflowExecutionService(ComfyClient client)
{
    public Task<ComfySubmissionResult> SubmitAsync(JsonObject workflow, CancellationToken cancellationToken) =>
        client.SubmitAsync(workflow, cancellationToken: cancellationToken);

    public async Task<CompletedWorkflow> RunAsync(JsonObject workflow, string requestedMediaType, CancellationToken cancellationToken,
        Func<ComfySubmissionResult, Task>? onSubmitted = null, Func<Task>? onSubmitting = null)
    {
        var clientId = Guid.NewGuid().ToString("N");
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(WebSocketUri(client.BaseUrl, clientId), cancellationToken);
        if (onSubmitting is not null) await onSubmitting();
        var submission = await client.SubmitAsync(workflow, clientId, cancellationToken);
        if (onSubmitted is not null) await onSubmitted(submission);
        await WaitForCompletionAsync(socket, submission.PromptId, cancellationToken);
        var history = await client.GetHistoryAsync(submission.PromptId, cancellationToken);
        var entry = history[submission.PromptId] as JsonObject
            ?? throw new InvalidOperationException($"ComfyUI returned no history for prompt '{submission.PromptId}'.");
        var output = ComfyClient.FindOutputs(entry["outputs"] as JsonObject ?? [])
            .FirstOrDefault(item => ComfyClient.MimeFor(item.Filename).StartsWith(requestedMediaType + "/", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Workflow completed without a {requestedMediaType} output.");
        var bytes = await client.FetchOutputAsync(output, cancellationToken);
        return new CompletedWorkflow(submission.PromptId, output, bytes, ComfyClient.MimeFor(output.Filename));
    }

    private static async Task WaitForCompletionAsync(ClientWebSocket socket, string promptId, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("ComfyUI WebSocket closed before the workflow completed.");
                if (received.MessageType == WebSocketMessageType.Text) message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            if (received.MessageType != WebSocketMessageType.Text) continue;
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())) as JsonObject;
            var type = payload?["type"]?.ToString();
            var data = payload?["data"] as JsonObject;
            if (!string.Equals(data?["prompt_id"]?.ToString(), promptId, StringComparison.Ordinal)) continue;
            if (type is "execution_error" or "execution_interrupted")
                throw new InvalidOperationException(data?["exception_message"]?.ToString() ?? $"ComfyUI reported {type}.");
            if (type == "execution_success" || (type == "executing" && data?["node"] is null)) return;
        }
    }

    private static Uri WebSocketUri(string baseUrl, string clientId)
    {
        var builder = new UriBuilder(baseUrl)
        {
            Scheme = baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/ws",
            Query = $"clientId={Uri.EscapeDataString(clientId)}"
        };
        return builder.Uri;
    }
}
