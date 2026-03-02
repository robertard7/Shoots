using System;
using Shoots.Contracts.Core.AI;

namespace Shoots.UI.Services;

public sealed record AiPanelVisibilityState(
    bool CanRenderAiPanel,
    bool CanRenderAiExplainButtons,
    bool CanRenderAiProviderStatus
);

public sealed class AiPanelVisibilityService
{
    public AiPanelVisibilityState Evaluate(AiPresentationPolicy presentation, AiAccessRole role)
    {
        if (presentation is null)
            throw new ArgumentNullException(nameof(presentation));

        if (presentation.EnterpriseMode && role == AiAccessRole.EndUser)
            return new AiPanelVisibilityState(false, false, false);

        return presentation.Visibility switch
        {
            AiVisibilityMode.Visible => new AiPanelVisibilityState(true, true, true),
            AiVisibilityMode.HiddenForEndUsers => role == AiAccessRole.EndUser
                ? new AiPanelVisibilityState(false, false, false)
                : new AiPanelVisibilityState(true, true, true),
            AiVisibilityMode.AdminOnly => role is AiAccessRole.Admin or AiAccessRole.Developer
                ? new AiPanelVisibilityState(true, true, true)
                : new AiPanelVisibilityState(false, false, false),
            _ => new AiPanelVisibilityState(true, true, true)
        };
    }
}
