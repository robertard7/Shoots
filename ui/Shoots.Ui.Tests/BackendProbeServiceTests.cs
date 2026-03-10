using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services.Backends;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BackendProbeServiceTests
{
    [Fact]
    public async Task Probe_timeout_produces_stable_error_code()
    {
        var service = new BackendProbeService(new TimeoutOllamaClient(), new HealthyQdrantClient());

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Equal("ui.ollama.timeout", status.ErrorCode);
    }

    [Fact]
    public async Task Probe_retries_once_for_retryable_ollama_failures()
    {
        var client = new SequenceOllamaClient(
            new OllamaTagsResult(false, new string[0], "ui.ollama.timeout", "timeout"),
            new OllamaTagsResult(true, new[] { "llama3" }, null, "ok"));
        var service = new BackendProbeService(client, new HealthyQdrantClient());

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.True(status.IsAvailable);
        Assert.Equal(2, client.CallCount);
        Assert.Contains("after retry", status.Summary);
    }

    [Fact]
    public void Endpoint_resolver_prefers_env_var_over_fallback()
    {
        const string expected = "http://ollama:11434";
        System.Environment.SetEnvironmentVariable("OLLAMA_HOST", expected);

        try
        {
            var actual = EndpointResolver.ResolveOllamaEndpoint();
            Assert.Equal(expected, actual);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("OLLAMA_HOST", null);
        }
    }

    private sealed class TimeoutOllamaClient : IOllamaClient
    {
        public Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new OllamaTagsResult(false, new string[0], "ui.ollama.timeout", "timeout"));
    }

    private sealed class SequenceOllamaClient : IOllamaClient
    {
        private readonly Queue<OllamaTagsResult> _results;

        public SequenceOllamaClient(params OllamaTagsResult[] results)
        {
            _results = new Queue<OllamaTagsResult>(results);
        }

        public int CallCount { get; private set; }

        public Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class HealthyQdrantClient : IQdrantClient
    {
        public Task<QdrantHealthResult> GetHealthAsync(CancellationToken cancellationToken)
            => Task.FromResult(new QdrantHealthResult(true, null, "ok"));
    }
}
