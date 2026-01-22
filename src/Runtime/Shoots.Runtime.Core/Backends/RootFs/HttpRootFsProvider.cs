using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core.Backends.RootFs;

public sealed class HttpRootFsProvider : IRootFsProvider
{
    private readonly HttpClient _httpClient;

    public HttpRootFsProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string ProviderId => "http";

    public bool CanHandle(RootFsDescriptor descriptor) =>
        descriptor.SourceType == RootFsSourceType.Http;

    public async Task<RootFsProvisionResult> ProvideAsync(
        RootFsProvisionRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Descriptor.DefaultUrl))
            throw new ArgumentException("DefaultUrl is required for http rootfs.");

        Directory.CreateDirectory(request.CachePath);
        var archivePath = Path.Combine(request.CachePath, "rootfs.tar.gz");

        using var response = await _httpClient.GetAsync(request.Descriptor.DefaultUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = File.Create(archivePath))
        {
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.Descriptor.Checksum))
        {
            var actual = ComputeSha256(archivePath);
            var expected = NormalizeChecksum(request.Descriptor.Checksum);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RootFS checksum mismatch.");
        }

        var metadata = new Dictionary<string, string>
        {
            ["url"] = request.Descriptor.DefaultUrl,
            ["archivePath"] = archivePath
        };

        var provenance = new RootFsProvenance(
            ProviderId,
            request.Descriptor.DefaultUrl,
            request.Descriptor.Checksum,
            DateTimeOffset.UtcNow,
            metadata);

        return new RootFsProvisionResult(provenance);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeChecksum(string checksum)
    {
        const string prefix = "sha256:";
        if (checksum.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return checksum[prefix.Length..];

        return checksum;
    }
}
