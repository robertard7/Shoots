using System;

namespace Shoots.UI.Services.Backends;

public static class EndpointResolver
{
    public static string ResolveOllamaEndpoint()
        => ResolveWithFallbacks("OLLAMA_HOST", "http://localhost:11434", "http://127.0.0.1:11434", "http://host.docker.internal:11434");

    public static string ResolveQdrantEndpoint()
        => ResolveWithFallbacks("QDRANT_URL", "http://localhost:6333", "http://127.0.0.1:6333", "http://host.docker.internal:6333");

    private static string ResolveWithFallbacks(string variable, params string[] candidates)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
