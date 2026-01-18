using Shoots.Providers.Abstractions;

namespace Shoots.Providers.Null;

public static class NullProviderRegistry
{
    public static ProviderRegistry Create()
    {
        var registry = new ProviderRegistry();
        registry.Register("null.local", NullAiProviderAdapter.Instance);
        return registry;
    }
}
