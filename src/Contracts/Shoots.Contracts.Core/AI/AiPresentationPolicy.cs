namespace Shoots.Contracts.Core.AI;

/// <summary>
/// Presentation policy for AI visibility and export controls.
/// </summary>
public sealed record AiPresentationPolicy(
    AiVisibilityMode Visibility,
    bool AllowAiPanelToggle,
    bool AllowCopyExport,
    bool EnterpriseMode
);
