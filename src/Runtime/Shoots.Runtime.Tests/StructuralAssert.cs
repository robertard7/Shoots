using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Shoots.Runtime.Tests;

internal static class StructuralAssert
{
    public static void Equal(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual))
            return;

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static string Normalize(object? value)
    {
        if (value is null)
            return "null";

        return JsonSerializer.Serialize(NormalizeValue(value));
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is null)
            return null;

        if (value is string)
            return value;

        if (value is IDictionary dictionary)
        {
            var normalized = new SortedDictionary<string, object?>(System.StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
                normalized[(string)entry.Key] = NormalizeValue(entry.Value);
            return normalized;
        }

        if (value is IEnumerable enumerable)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
                list.Add(NormalizeValue(item));
            return list;
        }

        return value;
    }
}
