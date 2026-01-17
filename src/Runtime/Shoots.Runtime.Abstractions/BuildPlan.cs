namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Deterministic plan produced for a build request.
/// </summary>
public sealed record BuildPlan(
    string PlanId,
    BuildRequest Request,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<BuildArtifact> Artifacts
);

public sealed record BuildStep(
    string Id,
    string Description
);
