using Shoots.ProviderAdapters.Abstractions;

namespace Shoots.ProviderAdapters.Null;

public static class NullProviderRegistry
{
    public static ProviderRegistry Create()
    {
        var registry = new ProviderRegistry();
        registry.Register("null.local", NullAiProviderAdapter.Instance);
        return registry;
    }
}
