using Shoots.Providers.Bridge;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderPresenceTripwireTests
{
    [Fact]
    public void Default_provider_registry_includes_embedded_provider()
    {
        var registry = ProviderRegistryFactory.CreateDefault();

        Assert.Contains("embedded.local", registry.Providers.Keys);
    }
}
