using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class DefaultAiPolicyResolver : IAiPolicyResolver
{
    public AiPresentationPolicy Resolve(AiAccessRole accessRole)
    {
        _ = accessRole;
        return new AiPresentationPolicy(
            AiVisibilityMode.Visible,
            AllowAiPanelToggle: true,
            AllowCopyExport: true,
            EnterpriseMode: false);
    }
}
