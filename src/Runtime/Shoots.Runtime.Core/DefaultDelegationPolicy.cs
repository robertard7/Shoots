using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class DefaultDelegationPolicy : IDelegationPolicy
{
    private static readonly ProviderId LocalProviderId = new("local");
    public string PolicyId => "local-only";

    public DelegationDecision Decide(BuildRequest request, BuildPlan plan)
    {
        _ = request ?? throw new ArgumentNullException(nameof(request));
        _ = plan ?? throw new ArgumentNullException(nameof(plan));

        return new DelegationDecision(
            new DelegationAuthority(
                ProviderId: LocalProviderId,
                Kind: ProviderKind.Local,
                PolicyId: PolicyId,
                AllowsDelegation: false)
        );
    }
}
