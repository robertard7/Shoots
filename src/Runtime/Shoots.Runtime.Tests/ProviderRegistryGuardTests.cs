using System;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderRegistryGuardTests
{
    [Fact]
    public void Embedded_provider_cannot_be_overridden()
    {
        var registry = new ProviderRegistry();
        registry.Register(ProviderRegistry.EmbeddedProviderId, new StubAdapter());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(ProviderRegistry.EmbeddedProviderId, new StubAdapter()));

        Assert.Contains("Embedded provider cannot be overridden", exception.Message);
    }

    [Fact]
    public void Registry_requires_embedded_provider()
    {
        var registry = new ProviderRegistry();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.EnsureEmbeddedProviderPrimary());

        Assert.Contains("Embedded provider is required", exception.Message);
    }

    [Fact]
    public void Registry_requires_embedded_provider_first()
    {
        var registry = new ProviderRegistry();
        registry.Register("fake.local", new StubAdapter());
        registry.Register(ProviderRegistry.EmbeddedProviderId, new StubAdapter());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.EnsureEmbeddedProviderPrimary());

        Assert.Contains("Embedded provider must be registered first", exception.Message);
    }

    [Fact]
    public void Registry_rejects_embedded_provider_wrong_id()
    {
        var registry = new ProviderRegistry();
        registry.Register("embedded.local-alt", new StubAdapter());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.EnsureEmbeddedProviderPrimary());

        Assert.Contains("Embedded provider is required", exception.Message);
    }

    [Fact]
    public void Registry_rejects_duplicate_provider_ids()
    {
        var registry = new ProviderRegistry();
        registry.Register("fake.local", new StubAdapter());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register("fake.local", new StubAdapter()));

        Assert.Contains("Provider already registered", exception.Message);
    }

    private sealed class StubAdapter : IAiProviderAdapter
    {
        public ToolSelectionDecision? RequestDecision(
            WorkOrder workOrder,
            RouteStep routeStep,
            string graphHash,
            string catalogHash,
            ToolCatalogSnapshot catalog)
        {
            return null;
        }
    }
}
