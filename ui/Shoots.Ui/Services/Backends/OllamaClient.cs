using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Services.Backends;

public sealed record OllamaTagsResult(bool IsSuccess, IReadOnlyList<string> ModelNames, string? ErrorCode, string? Summary);

public interface IOllamaClient
{
    Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken);
}

public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;

    public OllamaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaTagsResult(
                    false,
                    Array.Empty<string>(),
                    $"ui.ollama.bad_status.{(int)response.StatusCode}",
                    $"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                {
                    return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.bad_json", "Ollama tags payload was missing models array.");
                }

                var names = models.EnumerateArray()
                    .Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                return new OllamaTagsResult(true, names, null, names.Length == 0 ? "No models found." : "Models loaded.");
            }
            catch (JsonException)
            {
                return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.bad_json", "Ollama response could not be parsed.");
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.timeout", "Ollama request timed out.");
        }
        catch (TaskCanceledException)
        {
            return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.cancelled", "Ollama request was cancelled.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.connection_refused", "Could not reach Ollama endpoint: connection refused.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.HostNotFound)
        {
            return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.host_not_found", "Could not reach Ollama endpoint: host not found.");
        }
        catch (HttpRequestException)
        {
            return new OllamaTagsResult(false, Array.Empty<string>(), "ui.ollama.unreachable", "Could not reach Ollama endpoint.");
        }
    }
}
