using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using YamlDotNet.Core;

namespace ComfyUi.WorkflowMcp;

public class ToolFailureException(string code, string message, string nextStep, string? parameter = null)
    : ArgumentException(message)
{
    public string Code { get; } = code;
    public string NextStep { get; } = nextStep;
    public string? Parameter { get; } = parameter;

    public static ToolFailureException WorkflowNotFound(string id) => new(
        "workflow_not_found", $"Workflow '{id}' not found.", "Call workflow_list and choose an existing workflow_id.");

    public static ToolFailureException UnknownParameter(string id, string name) => new(
        "unknown_parameter", $"Workflow '{id}' has no parameter '{name}'.", "Call workflow_get_options and use an available parameter name.", name);

    public static ToolFailureException MissingDefault(string name) => new(
        "missing_default", $"Parameter '{name}' requires a YAML default.", $"Set defaults.{name} with workflow_configure.", name);
}

public static class ToolErrors
{
    public static JsonObject Describe(Exception exception, string nextStep)
    {
        var code = exception switch
        {
            ToolFailureException failure => failure.Code,
            JsonException or YamlException => "invalid_configuration",
            HttpRequestException or WebSocketException => "comfyui_connection_error",
            UnauthorizedAccessException => "access_denied",
            IOException => "file_error",
            ArgumentException or FormatException => "invalid_arguments",
            _ => "operation_failed"
        };
        var result = new JsonObject
        {
            ["success"] = false,
            ["error"] = code,
            ["message"] = exception.Message,
            ["next_step"] = exception switch
            {
                ToolFailureException failure => failure.NextStep,
                HttpRequestException or WebSocketException => "Check that ComfyUI is running at the configured URL, then retry.",
                JsonException or YamlException => "Correct the workflow JSON or YAML configuration, then retry.",
                _ => nextStep
            }
        };
        if (exception is ToolFailureException { Parameter: { } parameter }) result["parameter"] = parameter;
        else if (exception is ArgumentException { ParamName: { } argument }) result["parameter"] = argument;
        if (exception is GenerationParameterException { AllowedValues.Count: > 0 } generation)
            result["allowed_values"] = new JsonArray(generation.AllowedValues.Select(value => JsonValue.Create(value)).ToArray());
        if (exception is ComfyWorkflowRejectedException rejected)
            foreach (var (name, value) in rejected.Details()) result[name] = value?.DeepClone();
        return result;
    }

    public static CallToolResult Result(Exception exception, string nextStep)
    {
        var error = Describe(exception, nextStep);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(error),
            Content = [new TextContentBlock { Text = error.ToJsonString() }]
        };
    }
}
