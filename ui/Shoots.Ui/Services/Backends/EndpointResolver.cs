using System;

namespace Shoots.UI.Services.Backends;

public static class EndpointResolver
{
    public static string ResolveOllamaEndpoint()
        => Resolve("OLLAMA_HOST", "http://localhost:11434");

    public static string ResolveQdrantEndpoint()
        => Resolve("QDRANT_URL", "http://localhost:6333");

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
