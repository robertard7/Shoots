using System.Globalization;
using Shoots.Contracts.Core;

namespace Shoots.ProviderAdapters.Embedded;

internal sealed record ToolBindingValidationResult(
    bool IsValid,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unknown,
    string TypeError,
    IReadOnlyDictionary<string, object?> NormalizedBindings);

internal static class ToolBindingValidator
{
    public static ToolBindingValidationResult Validate(
        ToolSpec spec,
        IReadOnlyDictionary<string, object?> bindings)
    {
        var provided = new HashSet<string>(bindings.Keys, StringComparer.Ordinal);
        var known = spec.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);

        var missing = spec.Inputs
            .Where(input => input.Required && !provided.Contains(input.Name))
            .Select(input => input.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var unknown = provided
            .Where(name => !known.Contains(name))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        var typeError = string.Empty;

        foreach (var input in spec.Inputs)
        {
            if (!bindings.TryGetValue(input.Name, out var raw) || raw is null)
                continue;

            if (!TryNormalize(input.Type, raw, out var value))
            {
                typeError = $"{input.Name}:{input.Type}";
                break;
            }

            normalized[input.Name] = value;
        }

        var isValid = missing.Length == 0 && unknown.Length == 0 && typeError.Length == 0;
        return new ToolBindingValidationResult(isValid, missing, unknown, typeError, normalized);
    }

    private static bool TryNormalize(string type, object value, out object? normalized)
    {
        switch (type)
        {
            case "string":
                if (value is string s)
                {
                    normalized = s;
                    return true;
                }

                if (value is bool b)
                {
                    normalized = b ? "true" : "false";
                    return true;
                }

                if (value is IFormattable formattable)
                {
                    normalized = formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                }

                normalized = null;
                return false;

            case "bool":
                if (value is bool boolValue)
                {
                    normalized = boolValue;
                    return true;
                }

                if (value is string boolText && bool.TryParse(boolText, out var parsedBool))
                {
                    normalized = parsedBool;
                    return true;
                }

                normalized = null;
                return false;

            case "int":
                if (TryNormalizeInt(value, out var parsedInt))
                {
                    normalized = parsedInt;
                    return true;
                }

                normalized = null;
                return false;

            case "array":
                if (value is System.Collections.IEnumerable && value is not string)
                {
                    normalized = value;
                    return true;
                }

                normalized = null;
                return false;

            default:
                normalized = value;
                return true;
        }
    }

    private static bool TryNormalizeInt(object value, out int normalized)
    {
        switch (value)
        {
            case int i:
                normalized = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                normalized = (int)l;
                return true;
            case double d when d >= int.MinValue && d <= int.MaxValue && Math.Truncate(d) == d:
                normalized = Convert.ToInt32(d, CultureInfo.InvariantCulture);
                return true;
            case float f when f >= int.MinValue && f <= int.MaxValue && MathF.Truncate(f) == f:
                normalized = Convert.ToInt32(f, CultureInfo.InvariantCulture);
                return true;
            case decimal m when m >= int.MinValue && m <= int.MaxValue && decimal.Truncate(m) == m:
                normalized = decimal.ToInt32(m);
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                normalized = parsed;
                return true;
            default:
                normalized = 0;
                return false;
        }
    }
}
