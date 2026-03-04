using Shoots.UI.Services.Backends;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class EndpointResolverTests
{
    [Fact]
    public void Ollama_env_var_precedence_wins_when_absolute_http_url()
    {
        const string expected = "http://ollama.example:11434";
        System.Environment.SetEnvironmentVariable("OLLAMA_HOST", expected);

        try
        {
            Assert.Equal(expected, EndpointResolver.ResolveOllamaEndpoint());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("OLLAMA_HOST", null);
        }
    }

    [Fact]
    public void Ollama_invalid_env_var_falls_back_to_deterministic_default()
    {
        System.Environment.SetEnvironmentVariable("OLLAMA_HOST", "not-a-url");

        try
        {
            Assert.Equal("http://localhost:11434", EndpointResolver.ResolveOllamaEndpoint());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("OLLAMA_HOST", null);
        }
    }

    [Fact]
    public void Qdrant_invalid_scheme_falls_back_to_deterministic_default()
    {
        System.Environment.SetEnvironmentVariable("QDRANT_URL", "ftp://localhost:6333");

        try
        {
            Assert.Equal("http://localhost:6333", EndpointResolver.ResolveQdrantEndpoint());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("QDRANT_URL", null);
        }
    }

    [Fact]
    public void Resolved_endpoints_are_absolute_http_urls()
    {
        var ollama = EndpointResolver.ResolveOllamaEndpoint();
        var qdrant = EndpointResolver.ResolveQdrantEndpoint();

        Assert.True(System.Uri.TryCreate(ollama, System.UriKind.Absolute, out var ollamaUri));
        Assert.True(System.Uri.TryCreate(qdrant, System.UriKind.Absolute, out var qdrantUri));
        Assert.Contains(ollamaUri!.Scheme, new[] { System.Uri.UriSchemeHttp, System.Uri.UriSchemeHttps });
        Assert.Contains(qdrantUri!.Scheme, new[] { System.Uri.UriSchemeHttp, System.Uri.UriSchemeHttps });
    }
}
