using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core.Backends.RootFs;

public sealed class RootFsCache
{
    private const string ProvenanceFileName = "provenance.json";
    private readonly string _cacheRoot;

    public RootFsCache(string? cacheRoot = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoots",
            "RootFsCache");
    }

    public string CacheRoot => _cacheRoot;

    public string GetCachePath(RootFsDescriptor descriptor)
    {
        var hash = ComputeDescriptorHash(descriptor);
        return Path.Combine(_cacheRoot, hash);
    }

    public bool TryLoadProvenance(string cachePath, out RootFsProvenance provenance)
    {
        provenance = new RootFsProvenance(
            "unknown",
            "unknown",
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

        var path = Path.Combine(cachePath, ProvenanceFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<RootFsProvenance>(json, JsonOptions());
            if (loaded is null)
                return false;

            provenance = loaded;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SaveProvenance(string cachePath, RootFsProvenance provenance)
    {
        Directory.CreateDirectory(cachePath);
        var json = JsonSerializer.Serialize(provenance, JsonOptions());
        File.WriteAllText(Path.Combine(cachePath, ProvenanceFileName), json);
    }

    public void ClearCache(string cachePath)
    {
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, recursive: true);
    }

    private static string ComputeDescriptorHash(RootFsDescriptor descriptor)
    {
        var builder = new StringBuilder();
        builder.Append(descriptor.Id).Append('|');
        builder.Append(descriptor.DisplayName).Append('|');
        builder.Append(descriptor.SourceType).Append('|');
        builder.Append(descriptor.DefaultUrl ?? string.Empty).Append('|');
        builder.Append(descriptor.Checksum ?? string.Empty).Append('|');
        builder.Append(descriptor.License).Append('|');
        builder.Append(descriptor.Notes ?? string.Empty).Append('|');
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
