using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services.Backends;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class OllamaClientTests
{
    [Fact]
    public async Task Valid_tags_payload_is_sorted_deterministically()
    {
        var client = BuildClient(HttpStatusCode.OK, "{\"models\":[{\"name\":\"zeta\"},{\"name\":\"alpha\"}]}");

        var result = await client.GetTagsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "alpha", "zeta" }, result.ModelNames);
    }

    [Fact]
    public async Task Invalid_json_returns_stable_error()
    {
        var client = BuildClient(HttpStatusCode.OK, "not-json");

        var result = await client.GetTagsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ui.ollama.bad_json", result.ErrorCode);
    }

    [Fact]
    public async Task Bad_status_returns_stable_error()
    {
        var client = BuildClient(HttpStatusCode.BadGateway, "{}");

        var result = await client.GetTagsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ui.ollama.bad_status.502", result.ErrorCode);
    }

    private static OllamaClient BuildClient(HttpStatusCode code, string body)
    {
        var handler = new StubHandler(new HttpResponseMessage(code)
        {
            Content = new StringContent(body)
        });
        var http = new HttpClient(handler)
        {
            BaseAddress = new System.Uri("http://localhost:11434")
        };
        return new OllamaClient(http);
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
