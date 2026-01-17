namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Deterministic plan derived from a build request.
/// </summary>
/// <param name="PlanId">Derived hash identifier for the plan (deterministic).</param>
/// <param name="Request">Input request used to derive the plan.</param>
/// <param name="AuthorityProviderId">Authority provider identity assigned at planning time.</param>
/// <param name="AuthorityKind">Authority provider kind assigned at planning time.</param>
/// <param name="DelegationPolicyId">Deterministic delegation policy identifier.</param>
/// <param name="AllowsDelegation">True when delegation is permitted by policy.</param>
/// <param name="Steps">Ordered steps derived deterministically from the request.</param>
/// <param name="Artifacts">Ordered artifacts expected from executing the plan.</param>
public sealed record BuildPlan(
    string PlanId,
    BuildRequest Request,
    ProviderId AuthorityProviderId,
    ProviderKind AuthorityKind,
    string DelegationPolicyId,
    bool AllowsDelegation,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<BuildArtifact> Artifacts
);

/// <summary>
/// Deterministic step derived from a build request.
/// </summary>
/// <param name="Id">Stable step identifier.</param>
/// <param name="Description">Human-readable step description.</param>
public sealed record BuildStep(
    string Id,
    string Description
);
