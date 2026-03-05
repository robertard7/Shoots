using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed class ArtifactManager
{
    private readonly List<ArtifactRecord> _records = new();

    public void Reset()
    {
        _records.Clear();
    }

    public void Capture(string runPath, string stepId, string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        var artifactRoot = Path.Combine(runPath, "artifacts");
        Directory.CreateDirectory(artifactRoot);

        var fileName = $"{stepId}_{Path.GetFileName(outputPath)}";
        var targetPath = Path.Combine(artifactRoot, fileName);
        File.Copy(outputPath, targetPath, overwrite: true);

        var info = new FileInfo(targetPath);
        _records.Add(new ArtifactRecord(fileName, targetPath, ComputeHash(targetPath), info.Length));
    }

    public string WriteMetadata(string runPath, string planHash, string toolCatalogHash)
    {
        var metadataPath = Path.Combine(runPath, "artifact.json");
        var payload = new
        {
            plan_hash = planHash,
            tool_catalog_hash = toolCatalogHash,
            artifacts = _records
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        var artifactRoot = Path.Combine(runPath, "artifacts");
        Directory.CreateDirectory(artifactRoot);
        var manifestPath = Path.Combine(artifactRoot, "manifest.json");
        var manifest = new ArtifactManifest(_records.Select(static record => new ArtifactManifestEntry(record.Path, record.Bytes, record.Sha256)).ToArray());
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return metadataPath;
    }

    public ArtifactVerificationResult VerifyArtifacts(string runPath)
    {
        var manifestPath = Path.Combine(runPath, "artifacts", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new ArtifactVerificationResult(false, new[] { $"manifest missing: {manifestPath}" });
        }

        var manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(manifestPath)) ?? new ArtifactManifest(Array.Empty<ArtifactManifestEntry>());
        var errors = new List<string>();

        foreach (var entry in manifest.Files)
        {
            if (!File.Exists(entry.Path))
            {
                errors.Add($"missing file: {entry.Path}");
                continue;
            }

            var info = new FileInfo(entry.Path);
            if (info.Length != entry.Bytes)
            {
                errors.Add($"size mismatch: {entry.Path}");
            }

            var hash = ComputeHash(entry.Path);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"hash mismatch: {entry.Path}");
            }
        }

        return new ArtifactVerificationResult(errors.Count == 0, errors);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ArtifactRecord(string Name, string Path, string Sha256, long Bytes);
}

public sealed record ArtifactManifest(IReadOnlyList<ArtifactManifestEntry> Files);

public sealed record ArtifactManifestEntry(string Path, long Bytes, string Sha256);

public sealed record ArtifactVerificationResult(bool Ok, IReadOnlyList<string> Errors);
