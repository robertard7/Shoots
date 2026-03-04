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
        var orderedHits = hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.TokensMatched).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.FirstMatchOffset).ToArray();
        var selectedHits = orderedHits.Take(normalized.MaxSteps).ToArray();

        var evidence = new List<PlanStepEvidence>();
        var steps = selectedHits.Select((hit, idx) =>
        {
            var stepId = ComputeHash($"{requestHash}|{idx}|{hit.Path}")[..12];
            var stepEvidence = new PlanStepEvidence
            {
                StepId = stepId,
                HitId = hit.HitId,
                Path = hit.Path,
                SnippetHash = ComputeHash(hit.Excerpt),
                Range = "1-*"
            };
            evidence.Add(stepEvidence);

            return new Dictionary<string, object?>
            {
                ["stepId"] = stepId,
                ["kind"] = "RunTool",
                ["toolId"] = "linux.noop.v1",
                ["args"] = new Dictionary<string, object?>
                {
                    ["targetPath"] = hit.Path,
                    ["requiresNetwork"] = false
                },
                ["evidence"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["hitId"] = stepEvidence.HitId,
                        ["path"] = stepEvidence.Path,
                        ["snippetHash"] = stepEvidence.SnippetHash,
                        ["range"] = stepEvidence.Range
                    }
                },
                ["inputs"] = new[] { $"retrieval/hits/{idx}" },
                ["outputs"] = new[] { $"tool/{idx}/result.json" },
                ["expects"] = new[] { "exitCode==0" }
            };
        }).ToArray();

        var maxArgsBytes = 0;
        foreach (var step in steps)
        {
            if (!step.TryGetValue("args", out var argsObj))
            {
                continue;
            }

            var argsJson = JsonSerializer.Serialize(argsObj, RepoSliceJson.Options);
            maxArgsBytes = Math.Max(maxArgsBytes, Encoding.UTF8.GetByteCount(argsJson));
        }

        if (maxArgsBytes > normalized.MaxArgsBytes)
        {
            throw new InvalidOperationException($"builder.synthesis.args_exceeded:max={normalized.MaxArgsBytes};actual={maxArgsBytes}");
        }

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
        var planBytes = Encoding.UTF8.GetByteCount(semanticJson);
        if (planBytes > normalized.MaxTotalPlanBytes)
        {
            throw new InvalidOperationException($"builder.synthesis.plan_exceeded:max={normalized.MaxTotalPlanBytes};actual={planBytes}");
        }
        var planHash = ComputeHash(semanticJson);
        var evidencePayload = JsonSerializer.Serialize(evidence.OrderBy(x => x.StepId, StringComparer.Ordinal).ThenBy(x => x.HitId, StringComparer.Ordinal).ToArray(), RepoSliceJson.Options);
        var evidenceHash = ComputeHash(evidencePayload);

        var planEnvelope = new Dictionary<string, object?>(basePlan)
        {
            ["evidenceHash"] = evidenceHash,
            ["planHash"] = planHash
        };

        var planJson = JsonSerializer.Serialize(planEnvelope, RepoSliceJson.Options);

        return new PlanSynthesisResult
        {
            PlanJson = planJson,
            PlanHash = planHash,
            RequestHash = requestHash,
            EvidenceHash = evidenceHash,
            Evidence = evidence.OrderBy(x => x.StepId, StringComparer.Ordinal).ThenBy(x => x.HitId, StringComparer.Ordinal).ToArray(),
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
