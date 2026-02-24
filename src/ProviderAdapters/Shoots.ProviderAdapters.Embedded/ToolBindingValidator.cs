using Shoots.Contracts.Core;

namespace Shoots.ProviderAdapters.Embedded;

internal static class ToolBindingValidator
{
    public static (bool IsValid, IReadOnlyList<string> Missing, IReadOnlyList<string> Unknown, string? TypeError) Validate(
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

        foreach (var input in spec.Inputs)
        {
            if (!bindings.TryGetValue(input.Name, out var value) || value is null)
                continue;

            if (!IsValidType(input.Type, value))
            {
                return (false, missing, unknown, $"{input.Name}:{input.Type}");
            }
        }

        return (missing.Length == 0 && unknown.Length == 0, missing, unknown, null);
    }

    private static bool IsValidType(string type, object value)
    {
        return type switch
        {
            "string" => value is string,
            "bool" => value is bool,
            "int" => value is int or long,
            "array" => value is System.Collections.IEnumerable and not string,
            _ => true
        };
    }
}
