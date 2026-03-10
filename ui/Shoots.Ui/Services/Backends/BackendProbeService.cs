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
        const int maxAttempts = 2;
        OllamaTagsResult? result = null;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
            result = await _ollamaClient.GetTagsAsync(timeout.Token).ConfigureAwait(false);
            if (result.IsSuccess || !IsRetryableOllamaError(result.ErrorCode) || attempt >= maxAttempts)
                break;
        }

        result ??= new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.unreachable", "Could not reach Ollama endpoint.");
        var summary = result.IsSuccess
            ? (attempt > 1 ? $"Ollama healthy after retry ({attempt}/{maxAttempts})." : result.Summary)
            : $"Attempt {attempt}/{maxAttempts}: {result.Summary}";
        return new BackendStatus(
            BackendKind.Ollama,
            result.IsSuccess,
            result.ErrorCode,
            summary,
            DateTimeOffset.UtcNow,
            endpoint,
            result.IsSuccess ? null : summary).WithBounds();
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

    private static bool IsRetryableOllamaError(string? errorCode)
    {
        return string.Equals(errorCode, "ui.ollama.timeout", StringComparison.Ordinal) ||
               string.Equals(errorCode, "ui.ollama.unreachable", StringComparison.Ordinal) ||
               string.Equals(errorCode, "ui.ollama.connection_refused", StringComparison.Ordinal) ||
               string.Equals(errorCode, "ui.ollama.host_not_found", StringComparison.Ordinal);
    }
}
