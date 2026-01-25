using System;
using System.Collections.Generic;

namespace Shoots.Providers.Abstractions;

public sealed class ProviderRegistry
{
    public const string EmbeddedProviderId = "embedded.local";

    private readonly Dictionary<string, IAiProviderAdapter> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IAiProviderAdapter> Providers => _providers;

    public void Register(string providerId, IAiProviderAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("provider id is required", nameof(providerId));
        if (adapter is null)
            throw new ArgumentNullException(nameof(adapter));

        if (string.Equals(providerId, EmbeddedProviderId, StringComparison.OrdinalIgnoreCase)
            && _providers.ContainsKey(EmbeddedProviderId))
        {
            throw new InvalidOperationException("Embedded provider cannot be overridden.");
        }

        _providers[providerId] = adapter;
    }

    public IAiProviderAdapter? Get(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return null;

        return _providers.TryGetValue(providerId, out var adapter) ? adapter : null;
    }
}
