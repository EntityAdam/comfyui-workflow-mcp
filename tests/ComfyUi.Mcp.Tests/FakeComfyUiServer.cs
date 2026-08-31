using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ComfyUi.Mcp.Tests;

internal enum FakeGenerationOutcome
{
    Success,
    Failure,
    DisconnectThenRecover
}

internal sealed class FakeComfyUiServer(
    FakeGenerationOutcome outcome = FakeGenerationOutcome.Success) :
    IAsyncDisposable
{
    private readonly WebApplication _app = BuildApplication();
    private readonly Channel<WebSocket> _sockets =
        Channel.CreateUnbounded<WebSocket>();
    private readonly ConcurrentDictionary<string, JsonObject> _history = new();
    private readonly byte[] _image = CreatePng();
    private int _promptSequence;

    public string BaseUrl { get; private set; } = "";
    public ConcurrentBag<string> HistoryRequests { get; } = [];
    public JsonObject? LastPromptBody { get; private set; }
    public string? LastWebSocketClientId { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _app.UseWebSockets();
        _app.MapPost("/prompt", SubmitAsync);
        _app.MapGet("/history/{promptId}", History);
        _app.MapGet("/queue", () => Results.Json(new
        {
            queue_running = Array.Empty<object>(),
            queue_pending = Array.Empty<object>()
        }));
        _app.MapPost("/queue", () => Results.Json(new { success = true }));
        _app.MapPost("/interrupt", () => Results.Json(new { success = true }));
        _app.MapGet(
            "/view",
            () => Results.Bytes(_image, "image/png"));
        _app.Map("/ws", AcceptSocketAsync);

        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        BaseUrl = addresses.Addresses.Single();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<IResult> SubmitAsync(HttpRequest request)
    {
        LastPromptBody = await JsonNode.ParseAsync(
            request.Body, cancellationToken: request.HttpContext.RequestAborted)
            as JsonObject;
        var promptId =
            $"prompt-{Interlocked.Increment(ref _promptSequence)}";
        _ = RunGenerationAsync(promptId, request.HttpContext.RequestAborted);
        return Results.Json(new { prompt_id = promptId, number = 1 });
    }

    private IResult History(string promptId)
    {
        HistoryRequests.Add(promptId);
        return Results.Json(
            _history.TryGetValue(promptId, out var entry)
                ? new JsonObject { [promptId] = entry.DeepClone() }
                : new JsonObject());
    }

    private async Task AcceptSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        LastWebSocketClientId = context.Request.Query["clientId"];
        await _sockets.Writer.WriteAsync(
            socket, context.RequestAborted);
        var buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(
                    buffer.AsMemory(), context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) when (
            context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private async Task RunGenerationAsync(
        string promptId,
        CancellationToken cancellationToken)
    {
        try
        {
            var socket = await _sockets.Reader.ReadAsync(cancellationToken);
            await Task.Delay(75, cancellationToken);
            await SendAsync(
                socket,
                new JsonObject
                {
                    ["type"] = "executing",
                    ["data"] = new JsonObject
                    {
                        ["prompt_id"] = promptId,
                        ["node"] = "6"
                    }
                },
                cancellationToken);

            if (outcome == FakeGenerationOutcome.Failure)
            {
                await SendAsync(
                    socket,
                    new JsonObject
                    {
                        ["type"] = "execution_error",
                        ["data"] = new JsonObject
                        {
                            ["prompt_id"] = promptId,
                            ["node_id"] = "6",
                            ["exception_message"] = "synthetic failure"
                        }
                    },
                    cancellationToken);
                return;
            }

            var history = CompletedHistory();
            _history[promptId] = history;
            if (outcome == FakeGenerationOutcome.DisconnectThenRecover)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "synthetic disconnect",
                    cancellationToken);
                return;
            }

            await SendAsync(
                socket,
                new JsonObject
                {
                    ["type"] = "executed",
                    ["data"] = new JsonObject
                    {
                        ["prompt_id"] = promptId,
                        ["node"] = "9",
                        ["output"] = history["outputs"]!["9"]!.DeepClone()
                    }
                },
                cancellationToken);
            await SendAsync(
                socket,
                new JsonObject
                {
                    ["type"] = "execution_success",
                    ["data"] = new JsonObject
                    {
                        ["prompt_id"] = promptId
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task SendAsync(
        WebSocket socket,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static JsonObject CompletedHistory() =>
        new()
        {
            ["status"] = new JsonObject { ["status_str"] = "success" },
            ["outputs"] = new JsonObject
            {
                ["9"] = new JsonObject
                {
                    ["images"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["filename"] = "generated.png",
                            ["subfolder"] = "mcp",
                            ["type"] = "output"
                        }
                    }
                }
            }
        };

    private static byte[] CreatePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }
}
