using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.Contracts.Core;

public sealed record PlanSynthesisRequest
{
    public string PlanKind { get; init; } = "builder_v1";
    public string RetrievalHash { get; init; } = string.Empty;
    public string ProviderKind { get; init; } = string.Empty;
    public string EnvironmentKind { get; init; } = string.Empty;
    public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
    public string ProjectRoot { get; init; } = string.Empty;

    public PlanSynthesisRequest Normalize() => this with
    {
        PlanKind = (PlanKind ?? string.Empty).Trim(),
        RetrievalHash = (RetrievalHash ?? string.Empty).Trim(),
        ProviderKind = (ProviderKind ?? string.Empty).Trim(),
        EnvironmentKind = (EnvironmentKind ?? string.Empty).Trim(),
        Constraints = Constraints.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        ProjectRoot = (ProjectRoot ?? string.Empty).Replace('\\', '/').Trim()
    };

    public string ComputeRequestHash()
    {
        var payload = JsonSerializer.Serialize(Normalize(), RepoSliceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed record PlanStepEvidence
{
    public string StepId { get; init; } = string.Empty;
    public string HitId { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string SnippetHash { get; init; } = string.Empty;
    public string Range { get; init; } = string.Empty;
}

public sealed record PlanSynthesisStats
{
    public int RetrievedHitCount { get; init; }
    public int StepCount { get; init; }
    public int ToolCount { get; init; }
}

public sealed record PlanSynthesisResult
{
    public string PlanJson { get; init; } = string.Empty;
    public string PlanHash { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;
    public IReadOnlyList<PlanStepEvidence> Evidence { get; init; } = Array.Empty<PlanStepEvidence>();
    public PlanSynthesisStats Stats { get; init; } = new();
}
