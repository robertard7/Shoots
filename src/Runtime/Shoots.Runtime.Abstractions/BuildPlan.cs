namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Deterministic plan produced for a build request.
/// </summary>
public sealed record BuildPlan(
    string PlanId,
    BuildRequest Request,
    IReadOnlyList<BuildPlanStep> Steps,
    IReadOnlyList<BuildArtifact> Artifacts
);

public sealed record BuildPlanStep(
    string Id,
    string Description
);
