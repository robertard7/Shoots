namespace Shoots.Runtime.Abstractions;

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic plan derived from a build request.
/// </summary>
/// <param name="PlanId">Derived hash identifier for the plan (deterministic).</param>
/// <param name="Request">Input request used to derive the plan.</param>
/// <param name="Authority">Immutable authority assigned at planning time.</param>
/// <param name="Steps">Ordered steps derived deterministically from the request.</param>
/// <param name="Artifacts">Ordered artifacts expected from executing the plan.</param>
public sealed record BuildPlan(
    string PlanId,
    BuildRequest Request,
    DelegationAuthority Authority,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<BuildArtifact> Artifacts
);

// ⚠️ CONTRACT FREEZE
// Any change here requires:
// 1. New versioned type OR
// 2. Explicit RFC + test update
/// <summary>
/// Deterministic step derived from a build request.
/// </summary>
/// <param name="Id">Stable step identifier.</param>
/// <param name="Description">Human-readable step description.</param>
public sealed record BuildStep(
    string Id,
    string Description
);
