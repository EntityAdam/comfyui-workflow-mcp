using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;

namespace ComfyUi.WorkflowMcp;

public sealed partial class WorkflowConfigurationManager
{
    internal static bool HasAutomaticValue(WorkflowParameter parameter) =>
        parameter.Name == "seed" && parameter.Type == typeof(long);

    internal static bool RequiresCallerValue(WorkflowParameter parameter, WorkflowValidation validation) =>
        validation.Defaults[parameter.Name] is null && !HasAutomaticValue(parameter);

    internal void ValidateConfiguration(JsonObject workflow, WorkflowValidation validation)
    {
        WorkflowTransforms.ValidateTargets(workflow, validation.Transforms);
        var parameters = WorkflowManager.DiscoverParameters(workflow);
        var references = WorkflowTransforms.HasReferences(validation.Transforms);
        if (references && WorkflowTransforms.MediaParameters.Any(parameters.ContainsKey))
            throw WorkflowTransforms.Invalid("Reference options must not also be scalar placeholders.");
        bool IsMedia(string name) => references && WorkflowTransforms.MediaParameters.Contains(name, StringComparer.Ordinal);
        foreach (var name in validation.Defaults.Select(pair => pair.Key)
                     .Concat(validation.AllowedValues.Select(pair => pair.Key))
                     .Concat(validation.Constraints.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
        {
            if (!parameters.ContainsKey(name) && !IsMedia(name))
                throw new ToolFailureException("unknown_parameter", $"Workflow '{validation.WorkflowId}' has no parameter '{name}'.",
                    $"Remove '{name}' from defaults, allowed_values, and constraints, or enable its parameter or transform in the same update.", name);
            var type = parameters.GetValueOrDefault(name)?.Type;
            if (validation.Constraints.ContainsKey(name)) ValidateRules(name, validation.Constraints[name], type);
            if (validation.Constraints[name]?["required"]?.GetValue<bool>() == false && type is not null &&
                RequiresCallerValue(parameters[name], validation))
                throw InvalidConfiguration(name, $"Scalar parameter '{name}' cannot be optional without a default.");
            if (validation.AllowedValues.ContainsKey(name))
            {
                if (validation.AllowedValues[name] is not JsonArray allowed)
                    throw InvalidConfiguration(name, $"allowed_values.{name} must be an array.");
                foreach (var value in allowed)
                {
                    try
                    {
                        var typed = ValidateParameterValue(name, value, type);
                        ValidateValue(name, typed, validation);
                    }
                    catch (ArgumentException ex)
                    {
                        throw InvalidConfiguration(name, $"Invalid allowed value for '{name}': {ex.Message}");
                    }
                }
            }
            if (validation.Defaults.ContainsKey(name))
            {
                try
                {
                    var typed = ValidateParameterValue(name, validation.Defaults[name], type);
                    ValidateValue(name, typed, validation);
                }
                catch (ArgumentException ex)
                {
                    throw new GenerationParameterException(name, $"Invalid default for '{name}': {ex.Message}",
                        (ex as GenerationParameterException)?.AllowedValues, "invalid_default",
                        $"Set a valid defaults.{name} or update its validation rules in the same workflow_configure call.");
                }
            }
            else if (IsMedia(name) && validation.Constraints[name]?["required"]?.GetValue<bool>() != true)
            {
                try { ValidateValue(name, new JsonArray(), validation); }
                catch (GenerationParameterException)
                {
                    throw InvalidConfiguration(name, $"Optional list '{name}' must allow its empty fallback. Allow [], set a default, or set required: true.");
                }
            }
        }
        if (parameters.TryGetValue("seed", out var seed) && HasAutomaticValue(seed) && validation.Defaults["seed"] is null)
            _ = AutomaticSeed(validation, chooseRandom: false);
    }

    internal static long AutomaticSeed(WorkflowValidation validation, bool chooseRandom = true)
    {
        if (validation.AllowedValues["seed"] is JsonArray { Count: > 0 } allowed)
        {
            var candidates = allowed.Select(value => WorkflowManager.Coerce(value!, typeof(long)).GetValue<long>())
                .Where(value => value >= 0).ToArray();
            if (candidates.Length == 0)
                throw InvalidConfiguration("seed", "Automatic seed generation needs a nonnegative allowed seed. Set an explicit default to use a negative seed sentinel.");
            return candidates[chooseRandom ? Random.Shared.Next(candidates.Length) : 0];
        }
        decimal Bound(string name, decimal fallback)
        {
            var bound = validation.Constraints["seed"]?[name];
            if (bound is null) return fallback;
            if (decimal.TryParse(bound.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            // Bounds outside decimal's range are already known to be finite numbers.
            return bound.ToString().StartsWith('-') ? decimal.MinValue : decimal.MaxValue;
        }
        var minimum = Math.Max(0, decimal.Ceiling(Bound("minimum", 0)));
        var maximum = Math.Min(long.MaxValue, decimal.Floor(Bound("maximum", long.MaxValue)));
        if (minimum > maximum)
            throw InvalidConfiguration("seed", "Automatic seed generation needs a nonnegative integer within the configured bounds. Set an explicit default to use a negative seed sentinel.");
        var min = (long)minimum;
        var max = (long)maximum;
        return !chooseRandom || min == max ? min : Random.Shared.NextInt64(min, max == long.MaxValue ? max : max + 1);
    }

    private static JsonNode ValidateParameterValue(string name, JsonNode? value, Type? type)
    {
        if (type is null)
        {
            WorkflowTransforms.ValidateMedia(name, value);
            return value!;
        }
        if (value is null || WorkflowManager.ContainsParameterPlaceholder(value))
            throw new ArgumentException("Supply a literal scalar value without parameter placeholders.");
        return WorkflowManager.Coerce(value, type);
    }

    private static void ValidateRules(string name, JsonNode? value, Type? type)
    {
        if (value is not JsonObject rules || rules.Any(pair => pair.Key is not ("minimum" or "maximum" or "pattern" or "required")))
            throw InvalidConfiguration(name, $"constraints.{name} must contain only minimum, maximum, pattern, and required rules.");
        foreach (var bound in new[] { "minimum", "maximum" })
            if (rules.ContainsKey(bound) && (type != typeof(long) && type != typeof(double) ||
                rules[bound] is not JsonValue scalar || scalar.TryGetValue<string>(out _) ||
                !TryNumber(scalar, out var number) || !double.IsFinite(number)))
                throw InvalidConfiguration(name, $"{bound} for '{name}' must be a finite number and requires a numeric parameter.");
        if (rules["minimum"] is { } minimum && rules["maximum"] is { } maximum &&
            TryNumber(minimum, out var min) && TryNumber(maximum, out var max) && min > max)
            throw InvalidConfiguration(name, $"minimum for '{name}' cannot exceed maximum.");
        if (rules.ContainsKey("required") && (rules["required"] is not JsonValue required || !required.TryGetValue<bool>(out _)))
            throw InvalidConfiguration(name, $"required for '{name}' must be a boolean.");
        if (rules.ContainsKey("pattern"))
        {
            if (type != typeof(string) || WorkflowTransforms.String(rules["pattern"]) is not { } pattern)
                throw InvalidConfiguration(name, $"pattern for '{name}' must be a string and requires a text parameter.");
            try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { throw InvalidConfiguration(name, $"Invalid pattern for '{name}': {ex.Message}"); }
        }
    }

    private static ToolFailureException InvalidConfiguration(string? name, string message) => new(
        "invalid_configuration", message, "Correct the defaults, allowed_values, and constraints together with workflow_configure.", name);
}
