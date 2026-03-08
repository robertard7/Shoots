using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shoots.UI.Builder;

public static class RunVerificationService
{
    public static RunVerificationResult Verify(string runPath)
    {
        var errors = new List<string>();
        var runJsonPath = Path.Combine(runPath, "run.json");
        var manifestPath = Path.Combine(runPath, "artifacts", "manifest.json");
        var environmentPath = Path.Combine(runPath, "environment.json");
        var narratorPath = Path.Combine(runPath, "narrator.jsonl");
        var evidenceBundlePath = Path.Combine(runPath, "evidence_bundle.json");
        var catalogPath = "etc/ui.tools.catalog.json";
        var transcriptPath = Path.Combine(GetWorkspacePath(runPath), "notes", "chat_transcript.jsonl");

        if (!File.Exists(runJsonPath))
        {
            errors.Add($"missing run.json: {runJsonPath}");
            return new RunVerificationResult(false, false, false, false, false, false, false, false, errors);
        }

        var run = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(runJsonPath));
        if (run is null)
        {
            errors.Add("run.json parse failed");
            return new RunVerificationResult(false, false, false, false, false, false, false, false, errors);
        }

        if (!string.Equals(run.ContractVersion, ExecutionContract.Version, StringComparison.Ordinal))
        {
            errors.Add($"contract version mismatch: run={run.ContractVersion}; expected={ExecutionContract.Version}");
        }

        var manifestValid = File.Exists(manifestPath);
        if (!manifestValid)
        {
            errors.Add($"missing manifest: {manifestPath}");
        }

        var artifactsValid = false;
        if (manifestValid)
        {
            var artifactResult = new ArtifactManager().VerifyArtifacts(runPath);
            artifactsValid = artifactResult.Ok;
            foreach (var err in artifactResult.Errors)
            {
                errors.Add(err);
            }
        }

        var environmentValid = HashMatches(environmentPath, run.EnvironmentHash, errors, "environment");
        var narratorValid = HashMatches(narratorPath, run.NarratorHash, errors, "narrator");
        var bundleValid = HashMatches(evidenceBundlePath, run.EvidenceBundleHash, errors, "evidence bundle");

        var catalogValid = true;
        if (File.Exists(catalogPath) && !string.IsNullOrWhiteSpace(run.ToolCatalogHash))
        {
            var currentCatalogHash = ComputeHash(catalogPath);
            catalogValid = string.Equals(currentCatalogHash, run.ToolCatalogHash, StringComparison.OrdinalIgnoreCase);
            if (!catalogValid)
            {
                errors.Add($"catalog hash mismatch: run={run.ToolCatalogHash}; current={currentCatalogHash}");
            }
        }

        var transcriptValid = true;
        if (!string.IsNullOrWhiteSpace(run.TranscriptHash))
        {
            transcriptValid = HashMatches(transcriptPath, run.TranscriptHash, errors, "transcript");
        }

        if (string.Equals(run.HostTransport, "host", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(run.HostResponseOutcome))
        {
            errors.Add("host response metadata missing for host transport run");
        }

        var valid = manifestValid && artifactsValid && environmentValid && narratorValid && bundleValid && catalogValid && transcriptValid && errors.Count == 0;
        return new RunVerificationResult(valid, manifestValid, artifactsValid, environmentValid, narratorValid, bundleValid, catalogValid, transcriptValid, errors);
    }

    private static bool HashMatches(string path, string? expected, List<string> errors, string label)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            errors.Add($"{label} expected hash missing");
            return false;
        }

        if (!File.Exists(path))
        {
            errors.Add($"{label} file missing: {path}");
            return false;
        }

        var current = ComputeHash(path);
        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        errors.Add($"{label} hash mismatch: expected={expected}; actual={current}");
        return false;
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetWorkspacePath(string runPath)
        => Directory.GetParent(Directory.GetParent(Path.GetFullPath(runPath))!.FullName)!.FullName;
}
