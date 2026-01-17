namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Deterministic delegation decision derived from a request and plan.
/// </summary>
public sealed record DelegationDecision(
    ProviderId AuthorityProviderId,
    ProviderKind AuthorityKind,
    bool AllowsDelegation
);

/// <summary>
/// Pure policy that decides delegation and authority.
/// </summary>
public interface IDelegationPolicy
{
    string PolicyId { get; }

    DelegationDecision Decide(BuildRequest request, BuildPlan plan);
}
