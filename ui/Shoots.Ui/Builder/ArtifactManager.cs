using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed class ArtifactManager
{
    private readonly List<ArtifactRecord> _records = new();

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

        _records.Add(new ArtifactRecord(fileName, targetPath, ComputeHash(targetPath)));
    }

    public string WriteMetadata(string runPath)
    {
        var metadataPath = Path.Combine(runPath, "artifact.json");
        var payload = new { artifacts = _records };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return metadataPath;
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ArtifactRecord(string Name, string Path, string Sha256);
}
