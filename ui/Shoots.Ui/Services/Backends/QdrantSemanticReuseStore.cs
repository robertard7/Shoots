using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services;

namespace Shoots.UI.Services.Backends;

public sealed class QdrantSemanticReuseStore : ISemanticReuseVectorStore
{
    private const int VectorSize = 64;
    private readonly HttpClient _httpClient;

    public QdrantSemanticReuseStore(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task UpsertAsync(string repoKey, IReadOnlyList<SemanticReuseVectorPoint> points, CancellationToken cancellationToken)
    {
        if (points is null)
            throw new ArgumentNullException(nameof(points));
        if (points.Count == 0)
            return;

        var collectionName = BuildCollectionName(repoKey);
        await EnsureCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);

        var body = new
        {
            points = points.Select(point => new
            {
                id = point.DocumentId,
                vector = point.Vector,
                payload = new { document_id = point.DocumentId }
            })
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/collections/{collectionName}/points?wait=true")
        {
            Content = CreateJsonContent(body)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Qdrant upsert failed with status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    public async Task<IReadOnlyList<SemanticReuseVectorMatch>> SearchAsync(
        string repoKey,
        IReadOnlyList<float> vector,
        int limit,
        CancellationToken cancellationToken)
    {
        if (vector is null)
            throw new ArgumentNullException(nameof(vector));
        if (limit <= 0)
            return Array.Empty<SemanticReuseVectorMatch>();

        var collectionName = BuildCollectionName(repoKey);
        await EnsureCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);

        var body = new
        {
            vector,
            limit,
            with_payload = false,
            with_vector = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/collections/{collectionName}/points/search")
        {
            Content = CreateJsonContent(body)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Qdrant search failed with status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SemanticReuseVectorMatch>();
        }

        var matches = new List<SemanticReuseVectorMatch>();
        foreach (var item in resultElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) ||
                !item.TryGetProperty("score", out var scoreElement))
            {
                continue;
            }

            var id = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(id))
                continue;

            matches.Add(new SemanticReuseVectorMatch(id, scoreElement.GetDouble()));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.DocumentId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var body = new
        {
            vectors = new
            {
                size = VectorSize,
                distance = "Cosine"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/collections/{collectionName}")
        {
            Content = CreateJsonContent(body)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            return;

        throw new HttpRequestException(
            $"Qdrant collection ensure failed with status {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private static StringContent CreateJsonContent<T>(T body)
        => new(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

    private static string BuildCollectionName(string repoKey)
    {
        var hash = ComputeDeterministicHash(repoKey ?? string.Empty);
        return $"shoots_semantic_reuse_{hash[..12]}";
    }

    private static string ComputeDeterministicHash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
