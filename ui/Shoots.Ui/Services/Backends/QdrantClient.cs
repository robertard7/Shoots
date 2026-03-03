using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Services.Backends;

public sealed record QdrantHealthResult(bool IsSuccess, string? ErrorCode, string? Summary);

public interface IQdrantClient
{
    Task<QdrantHealthResult> GetHealthAsync(CancellationToken cancellationToken);
}

public sealed class QdrantClient : IQdrantClient
{
    private readonly HttpClient _httpClient;

    public QdrantClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<QdrantHealthResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/healthz", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new QdrantHealthResult(false, $"ui.qdrant.bad_status.{(int)response.StatusCode}", "Qdrant returned non-success status.");
            }

            return new QdrantHealthResult(true, null, "Qdrant is healthy.");
        }
        catch (TaskCanceledException)
        {
            return new QdrantHealthResult(false, "ui.qdrant.timeout", "Qdrant probe timed out.");
        }
        catch (HttpRequestException)
        {
            return new QdrantHealthResult(false, "ui.qdrant.unreachable", "Could not reach Qdrant endpoint.");
        }
    }
}
