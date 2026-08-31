using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class GenerationParameterException(
    string parameter,
    string message,
    IReadOnlyList<string>? allowedValues = null) : ArgumentException(message)
{
    public string Parameter { get; } = parameter;
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues ?? [];
}

public sealed class GenerationOptionsService(
    WorkflowManager workflows,
    WorkflowConfigurationManager configuration)
{
    public object GetOptions(string workflowId)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        var validation = configuration.Load(workflowId);
        var parameters = workflows.ExtractParameters(workflow);
        return new
        {
            workflow_id = workflowId,
            media_type = validation.MediaType,
            parameters = parameters.Values.ToDictionary(parameter => parameter.Name, parameter => new
            {
                type = TypeName(parameter.Type),
                required = validation.Constraints[parameter.Name]?["required"]?.GetValue<bool?>()
                    ?? validation.Defaults[parameter.Name] is null,
                @default = validation.Defaults[parameter.Name],
                allowed_values = validation.AllowedValues[parameter.Name],
                constraints = validation.Constraints[parameter.Name]
            })
        };
    }

    public ResolvedWorkflowExecution Resolve(string workflowId, JsonObject callValues)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw new ArgumentException($"Workflow '{workflowId}' not found.");
        var validation = configuration.Load(workflowId);
        var parameters = workflows.ExtractParameters(workflow);
        var values = validation.Defaults.DeepClone().AsObject();
        foreach (var (key, value) in callValues)
            values[Normalize(key)] = value?.DeepClone();

        var unknown = values.Select(pair => pair.Key)
            .Where(key => !parameters.ContainsKey(key)).ToArray();
        if (unknown.Length > 0)
            throw new GenerationParameterException(unknown[0], $"Workflow '{workflowId}' has no parameter '{unknown[0]}'.");

        foreach (var parameter in parameters.Values)
        {
            if (values[parameter.Name] is null && parameter.Name == "seed" && parameter.Type == typeof(long))
                values[parameter.Name] = Random.Shared.NextInt64(0, long.MaxValue);
            if (values[parameter.Name] is null)
                throw new GenerationParameterException(parameter.Name, $"No value was provided for '{parameter.Name}'. Run setup to configure a default or supply it in the tool call.");
            configuration.ValidateValue(parameter.Name, values[parameter.Name]!, validation);
        }
        return new ResolvedWorkflowExecution(workflowId, validation.MediaType, values);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('-', '_');
    private static string TypeName(Type type) => type == typeof(long) ? "integer" : type == typeof(double) ? "number" : type == typeof(bool) ? "boolean" : "string";
}
