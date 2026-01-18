using Shoots.Providers.Abstractions;
using Shoots.Providers.Fake;
using Shoots.Providers.Null;

namespace Shoots.Providers.Bridge;

public static class ProviderRegistryFactory
{
    public static ProviderRegistry CreateDefault()
    {
        var registry = new ProviderRegistry();
        registry.Register("fake.local", new FakeAiProviderAdapter());
        registry.Register("null.local", NullAiProviderAdapter.Instance);
        return registry;
    }
}
