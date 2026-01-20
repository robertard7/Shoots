using System;
using Shoots.Contracts.Core;

namespace Shoots.Providers.Abstractions;

public static class ProviderGuards
{
    public static void AgainstNull(object? value, string name)
    {
        if (value is null)
            throw new ArgumentNullException(name);
    }

    public static void AgainstNullOrWhiteSpace(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }

    public static string RequireOutput(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"{name} is required");

        return value;
    }

    public static ToolCatalogSnapshot RequireCatalog(ToolCatalogSnapshot? catalog)
    {
        if (catalog is null)
            throw new ArgumentNullException(nameof(catalog));

        return catalog;
    }
}
