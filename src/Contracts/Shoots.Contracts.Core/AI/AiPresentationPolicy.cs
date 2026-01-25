namespace Shoots.Contracts.Core.AI;

/// <summary>
/// Presentation policy for AI visibility and export controls.
/// </summary>
public sealed record AiPresentationPolicy(
    AiVisibilityMode Visibility,
    bool AllowAiPanelToggle,
    bool AllowCopyExport,
    bool EnterpriseMode
)
{
    public const int ContractVersion = 1;

    public const string ContractShape = "Visibility|AllowAiPanelToggle|AllowCopyExport|EnterpriseMode";
}
