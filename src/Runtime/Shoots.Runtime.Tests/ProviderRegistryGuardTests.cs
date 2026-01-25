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
