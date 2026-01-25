using Shoots.Providers.Bridge;
using Shoots.Providers.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderPresenceTripwireTests
{
    [Fact]
    public void Default_provider_registry_includes_embedded_provider()
    {
        var registry = ProviderRegistryFactory.CreateDefault();

        Assert.Contains(ProviderRegistry.EmbeddedProviderId, registry.Providers.Keys);
        Assert.Equal(ProviderRegistry.EmbeddedProviderId, registry.RegistrationOrder[0]);
    }
}
