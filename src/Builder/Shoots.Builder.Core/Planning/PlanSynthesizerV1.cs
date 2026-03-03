using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;

namespace Shoots.Builder.Core.Planning;

public sealed class PlanSynthesizerV1
{
    public PlanSynthesisResult Synthesize(PlanSynthesisRequest request, IReadOnlyList<RetrievalHit> hits)
    {
        var normalized = request.Normalize();
        var requestHash = normalized.ComputeRequestHash();
        var orderedHits = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Path, StringComparer.Ordinal).ToArray();

        var steps = orderedHits.Take(3).Select((hit, idx) => new Dictionary<string, object?>
        {
            ["stepId"] = ComputeHash($"{requestHash}|{idx}|{hit.Path}")[..12],
            ["kind"] = "RunTool",
            ["toolId"] = "linux.noop.v1",
            ["args"] = new Dictionary<string, object?>
            {
                ["targetPath"] = hit.Path,
                ["requiresNetwork"] = false
            },
            ["inputs"] = new[] { $"retrieval/hits/{idx}" },
            ["outputs"] = new[] { $"tool/{idx}/result.json" },
            ["expects"] = new[] { "exitCode==0" }
        }).ToArray();

        var basePlan = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["planKind"] = normalized.PlanKind,
            ["inputs"] = new Dictionary<string, object?>
            {
                ["requestHash"] = requestHash,
                ["retrievalHash"] = normalized.RetrievalHash,
                ["constraints"] = normalized.Constraints,
                ["projectRoot"] = normalized.ProjectRoot
            },
            ["providerRef"] = normalized.ProviderKind,
            ["envRef"] = normalized.EnvironmentKind,
            ["steps"] = steps.OrderBy(x => x["stepId"]?.ToString(), StringComparer.Ordinal).ToArray()
        };

        var semanticJson = JsonSerializer.Serialize(basePlan, RepoSliceJson.Options);
        var planHash = ComputeHash(semanticJson);
        var planEnvelope = new Dictionary<string, object?>(basePlan)
        {
            ["planHash"] = planHash
        };

        var planJson = JsonSerializer.Serialize(planEnvelope, RepoSliceJson.Options);

        return new PlanSynthesisResult
        {
            PlanJson = planJson,
            PlanHash = planHash,
            RequestHash = requestHash,
            Stats = new PlanSynthesisStats
            {
                RetrievedHitCount = orderedHits.Length,
                StepCount = steps.Length,
                ToolCount = steps.Select(s => s["toolId"]?.ToString() ?? string.Empty).Distinct(StringComparer.Ordinal).Count()
            }
        };
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
