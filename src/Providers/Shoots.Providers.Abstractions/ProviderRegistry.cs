using System;
using System.Collections.Generic;

namespace Shoots.Providers.Abstractions;

public sealed class ProviderRegistry
{
    public const string EmbeddedProviderId = "embedded.local";

    private readonly List<string> _registrationOrder = new();
    private readonly Dictionary<string, IAiProviderAdapter> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IAiProviderAdapter> Providers => _providers;
    public IReadOnlyList<string> RegistrationOrder => _registrationOrder;

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

        if (_providers.ContainsKey(providerId))
            throw new InvalidOperationException($"Provider already registered: {providerId}");

        if (!_providers.ContainsKey(providerId))
            _registrationOrder.Add(providerId);

        _providers[providerId] = adapter;
    }

    public IAiProviderAdapter? Get(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return null;

        return _providers.TryGetValue(providerId, out var adapter) ? adapter : null;
    }

    public void EnsureEmbeddedProviderPrimary()
    {
        if (!_providers.ContainsKey(EmbeddedProviderId))
            throw new InvalidOperationException("Embedded provider is required.");

        if (_registrationOrder.Count == 0 ||
            !string.Equals(_registrationOrder[0], EmbeddedProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Embedded provider must be registered first.");
        }
    }
}
