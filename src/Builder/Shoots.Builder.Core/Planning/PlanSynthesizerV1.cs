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

        var planObj = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["planKind"] = normalized.PlanKind,
            ["retrievalHash"] = normalized.RetrievalHash,
            ["requestHash"] = requestHash,
            ["providerKind"] = normalized.ProviderKind,
            ["environmentKind"] = normalized.EnvironmentKind,
            ["projectRoot"] = normalized.ProjectRoot,
            ["steps"] = steps
        };

        var planJson = JsonSerializer.Serialize(planObj, RepoSliceJson.Options);
        var planHash = ComputeHash(planJson);

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
