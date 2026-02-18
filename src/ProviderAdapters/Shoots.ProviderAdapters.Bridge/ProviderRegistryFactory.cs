using Shoots.ProviderAdapters.Abstractions;
using Shoots.ProviderAdapters.Embedded;
using Shoots.ProviderAdapters.Fake;
using Shoots.ProviderAdapters.Ollama;
using Shoots.ProviderAdapters.Null;

namespace Shoots.ProviderAdapters.Bridge;

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
