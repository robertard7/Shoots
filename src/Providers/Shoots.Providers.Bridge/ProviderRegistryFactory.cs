using Shoots.Providers.Abstractions;
using Shoots.Providers.Embedded;
using Shoots.Providers.Fake;
using Shoots.Providers.Ollama;
using Shoots.Providers.Null;

namespace Shoots.Providers.Bridge;

public static class ProviderRegistryFactory
{
    public static ProviderRegistry CreateDefault(OllamaProviderSettings? ollamaSettings = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(ProviderRegistry.EmbeddedProviderId, new EmbeddedAiProviderAdapter());
        registry.Register("fake.local", new FakeAiProviderAdapter());
        registry.Register("null.local", NullAiProviderAdapter.Instance);
        if (ollamaSettings is not null)
            registry.Register("ollama.local", new OllamaAiProviderAdapter(ollamaSettings));
        registry.EnsureEmbeddedProviderPrimary();
        return registry;
    }
}
