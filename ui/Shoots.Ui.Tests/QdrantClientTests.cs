using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services.Backends;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class QdrantClientTests
{
    [Fact]
    public async Task Health_success_is_available()
    {
        var client = BuildClient(HttpStatusCode.OK, "ok");

        var result = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task Health_bad_status_returns_stable_error()
    {
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "down");

        var result = await client.GetHealthAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ui.qdrant.bad_status.503", result.ErrorCode);
    }

    private static QdrantClient BuildClient(HttpStatusCode code, string body)
    {
        var handler = new StubHandler(new HttpResponseMessage(code)
        {
            Content = new StringContent(body)
        });
        var http = new HttpClient(handler)
        {
            BaseAddress = new System.Uri("http://localhost:6333")
        };
        return new QdrantClient(http);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
