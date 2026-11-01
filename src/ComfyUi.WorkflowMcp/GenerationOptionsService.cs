using System.Text.Json.Nodes;

namespace ComfyUi.WorkflowMcp;

public sealed class GenerationParameterException(
    string parameter,
    string message,
    IReadOnlyList<string>? allowedValues = null,
    string code = "invalid_value",
    string? nextStep = null) : ToolFailureException(code, message, nextStep ?? $"Read workflow_get_options and correct the value for '{parameter}'.", parameter)
{
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues ?? [];
}

public sealed class GenerationOptionsService(
    WorkflowManager workflows,
    WorkflowConfigurationManager configuration)
{
    public object GetOptions(string workflowId)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        var validation = configuration.Load(workflowId);
        var parameters = workflows.ExtractParameters(workflow);
        configuration.ValidateConfiguration(workflow, validation);
        var parameterOptions = parameters.Values.ToDictionary(parameter => parameter.Name, parameter => (object)new
        {
            type = TypeName(parameter.Type),
            bindings = parameter.Bindings.Select(binding => new { node_id = binding.NodeId, input_name = binding.InputName }).Distinct().ToArray(),
            required = WorkflowConfigurationManager.RequiresCallerValue(parameter, validation),
            @default = validation.Defaults[parameter.Name],
            allowed_values = validation.AllowedValues[parameter.Name],
            constraints = validation.Constraints[parameter.Name]
        });
        if (WorkflowTransforms.HasReferences(validation.Transforms))
            foreach (var (name, schema) in WorkflowTransforms.ParameterSchemas())
            {
                if (parameters.ContainsKey(name)) throw WorkflowTransforms.Invalid($"Reference option '{name}' conflicts with a scalar placeholder.");
                var entry = schema!.DeepClone().AsObject();
                var required = validation.Defaults[name] is null && validation.Constraints[name]?["required"]?.GetValue<bool>() == true;
                entry["required"] = required;
                entry["default"] = validation.Defaults[name]?.DeepClone() ?? (required ? null : new JsonArray());
                entry["bindings"] = new JsonArray();
                parameterOptions[name] = entry;
            }
        return new
        {
            workflow_id = workflowId,
            description = validation.Description,
            media_type = validation.MediaType,
            wait_for_completion = validation.WaitForCompletion ?? true,
            defaults = validation.Defaults,
            allowed_values = validation.AllowedValues,
            constraints = validation.Constraints,
            transforms = validation.Transforms,
            parameters = parameterOptions
        };
    }

    public ResolvedWorkflowExecution Resolve(string workflowId, JsonObject callValues)
    {
        var workflow = workflows.Load(workflowId)
            ?? throw ToolFailureException.WorkflowNotFound(workflowId);
        var validation = configuration.Load(workflowId);
        return Resolve(workflowId, callValues, workflow, validation);
    }

    internal ResolvedWorkflowExecution Resolve(string workflowId, JsonObject callValues, JsonObject workflow, WorkflowValidation validation)
    {
        configuration.ValidateConfiguration(workflow, validation);
        var parameters = workflows.ExtractParameters(workflow);
        var references = WorkflowTransforms.HasReferences(validation.Transforms);
        if (references && WorkflowTransforms.MediaParameters.Any(parameters.ContainsKey))
            throw WorkflowTransforms.Invalid("Reference options must not also be scalar placeholders.");
        var values = validation.Defaults.DeepClone().AsObject();
        foreach (var (key, value) in callValues)
            values[Normalize(key)] = value?.DeepClone();

        var unknown = values.Select(pair => pair.Key)
            .Where(key => !parameters.ContainsKey(key) && !(references && WorkflowTransforms.MediaParameters.Contains(key, StringComparer.Ordinal))).ToArray();
        if (unknown.Length > 0)
            throw ToolFailureException.UnknownParameter(workflowId, unknown[0]);

        if (references)
            foreach (var name in WorkflowTransforms.MediaParameters)
            {
                if (!values.ContainsKey(name))
                {
                    if (validation.Constraints[name]?["required"]?.GetValue<bool>() == true)
                        throw new GenerationParameterException(name, $"No value was provided for '{name}'.", code: "missing_default");
                    values[name] = new JsonArray();
                }
                WorkflowTransforms.ValidateMedia(name, values[name]);
                configuration.ValidateValue(name, values[name]!, validation);
            }

        foreach (var parameter in parameters.Values)
        {
            if (values[parameter.Name] is null && WorkflowConfigurationManager.HasAutomaticValue(parameter))
                values[parameter.Name] = WorkflowConfigurationManager.AutomaticSeed(validation);
            if (values[parameter.Name] is null)
                throw new GenerationParameterException(parameter.Name, $"No value was provided for '{parameter.Name}'.", code: "missing_default", nextStep: $"Set defaults.{parameter.Name} with workflow_configure.");
            try { values[parameter.Name] = WorkflowManager.Coerce(values[parameter.Name]!, parameter.Type); }
            catch (ArgumentException ex) { throw new GenerationParameterException(parameter.Name, ex.Message); }
            configuration.ValidateValue(parameter.Name, values[parameter.Name]!, validation);
        }
        return new ResolvedWorkflowExecution(workflowId, validation.MediaType, values);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('-', '_');
    private static string TypeName(Type type) => type == typeof(long) ? "integer" : type == typeof(double) ? "number" : type == typeof(bool) ? "boolean" : "string";
}
