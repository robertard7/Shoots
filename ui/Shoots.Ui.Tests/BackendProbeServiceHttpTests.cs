using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services.Backends;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BackendProbeServiceHttpTests
{
    [Fact]
    public async Task Probe_ollama_timeout_maps_to_stable_error_code()
    {
        var service = BuildService(new TimeoutHandler());

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Equal("ui.ollama.timeout", status.ErrorCode);
    }

    [Fact]
    public async Task Probe_ollama_unreachable_maps_to_stable_error_code()
    {
        var service = BuildService(new ThrowingHandler(new HttpRequestException("no route")));

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Equal("ui.ollama.unreachable", status.ErrorCode);
    }

    [Fact]
    public async Task Probe_ollama_bad_status_maps_to_stable_error_code()
    {
        var service = BuildService(new StaticResponseHandler(HttpStatusCode.BadGateway, "{}"));

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Equal("ui.ollama.bad_status.502", status.ErrorCode);
    }

    [Fact]
    public async Task Probe_ollama_bad_json_maps_to_stable_error_code()
    {
        var service = BuildService(new StaticResponseHandler(HttpStatusCode.OK, "not-json"));

        var status = await service.ProbeOllamaAsync(CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Equal("ui.ollama.bad_json", status.ErrorCode);
    }

    private static BackendProbeService BuildService(HttpMessageHandler ollamaHandler)
    {
        var ollamaHttp = new HttpClient(ollamaHandler)
        {
            BaseAddress = new System.Uri("http://localhost:11434")
        };

        return new BackendProbeService(new OllamaClient(ollamaHttp), new HealthyQdrantClient());
    }

    private sealed class HealthyQdrantClient : IQdrantClient
    {
        public Task<QdrantHealthResult> GetHealthAsync(CancellationToken cancellationToken)
            => Task.FromResult(new QdrantHealthResult(true, null, "ok"));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;

        public ThrowingHandler(Exception ex)
        {
            _ex = ex;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_ex);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StaticResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}
