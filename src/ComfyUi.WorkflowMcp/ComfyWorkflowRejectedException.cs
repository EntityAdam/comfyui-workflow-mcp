using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class ComfyWorkflowRejectedException(int statusCode, JsonObject? response, string responseText)
    : ToolFailureException("workflow_rejected",
        $"ComfyUI rejected workflow or output branches (HTTP {statusCode}).",
        response?["prompt_id"] is { } prompt
            ? $"Prompt '{prompt}' may already have queued branches. Inspect or cancel that job before resubmitting, then correct the structured node_errors."
            : statusCode is >= 200 and < 300 or >= 500
                ? "Acceptance could not be confirmed. Inspect the queue before resubmitting, then use the structured errors or response_body to correct the failure."
                : "Correct the structured node_errors and comfy_error before submitting again.")
{
    private readonly JsonObject? _response = response?.DeepClone().AsObject();

    internal JsonObject Details()
    {
        var details = new JsonObject { ["status_code"] = statusCode };
        if (_response?["error"] is { } error) details["comfy_error"] = error.DeepClone();
        if (_response?["prompt_id"] is { } prompt) details["prompt_id"] = prompt.DeepClone();
        // A returned prompt ID can mean other output branches were accepted; never silently retry.
        details["may_have_queued"] = _response?["prompt_id"] is not null || statusCode is >= 200 and < 300 or >= 500;
        if (_response?["node_errors"] is { } nodeErrors)
        {
            details["node_errors"] = nodeErrors.DeepClone();
            var validationErrors = new JsonArray();
            if (nodeErrors is JsonObject nodes)
                foreach (var (nodeId, node) in nodes)
                    if (node is JsonObject entry && entry["errors"] is JsonArray errors)
                        foreach (var item in errors.OfType<JsonObject>())
                        {
                            var validation = item.DeepClone().AsObject();
                            validation["node_id"] = nodeId;
                            if (entry["class_type"] is { } classType) validation["class_type"] = classType.DeepClone();
                            if (item["extra_info"] is JsonObject extra && extra["input_name"] is { } inputName)
                                validation["input_name"] = inputName.DeepClone();
                            validationErrors.Add(validation);
                        }
            details["validation_errors"] = validationErrors;
        }
        if (_response is null) details["response_body"] = responseText;
        return details;
    }
}
