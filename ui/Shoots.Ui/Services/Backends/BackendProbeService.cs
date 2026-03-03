using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Services.Backends;

public interface IBackendProbeService
{
    Task<BackendStatus> ProbeOllamaAsync(CancellationToken cancellationToken);

    Task<BackendStatus> ProbeQdrantAsync(CancellationToken cancellationToken);
}

public sealed class BackendProbeService : IBackendProbeService
{
    private readonly IOllamaClient _ollamaClient;
    private readonly IQdrantClient _qdrantClient;

    public BackendProbeService(IOllamaClient ollamaClient, IQdrantClient qdrantClient)
    {
        _ollamaClient = ollamaClient;
        _qdrantClient = qdrantClient;
    }

    public async Task<BackendStatus> ProbeOllamaAsync(CancellationToken cancellationToken)
    {
        var endpoint = EndpointResolver.ResolveOllamaEndpoint();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
        var result = await _ollamaClient.GetTagsAsync(timeout.Token).ConfigureAwait(false);
        return new BackendStatus(
            BackendKind.Ollama,
            result.IsSuccess,
            result.ErrorCode,
            result.Summary,
            DateTimeOffset.UtcNow,
            endpoint,
            result.IsSuccess ? null : result.Summary).WithBounds();
    }

    public async Task<BackendStatus> ProbeQdrantAsync(CancellationToken cancellationToken)
    {
        var endpoint = EndpointResolver.ResolveQdrantEndpoint();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
        var result = await _qdrantClient.GetHealthAsync(timeout.Token).ConfigureAwait(false);
        return new BackendStatus(
            BackendKind.Qdrant,
            result.IsSuccess,
            result.ErrorCode,
            result.Summary,
            DateTimeOffset.UtcNow,
            endpoint,
            result.IsSuccess ? null : result.Summary).WithBounds();
    }
}
