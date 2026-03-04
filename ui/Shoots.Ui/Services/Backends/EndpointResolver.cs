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
        if (TryNormalizeAbsoluteHttpUrl(value, out var normalizedFromEnv))
        {
            return normalizedFromEnv;
        }

        foreach (var candidate in candidates)
        {
            if (TryNormalizeAbsoluteHttpUrl(candidate, out var normalizedCandidate))
            {
                return normalizedCandidate;
            }
        }

        return string.Empty;
    }

    private static bool TryNormalizeAbsoluteHttpUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }
}
