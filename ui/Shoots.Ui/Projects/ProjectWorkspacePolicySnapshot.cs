using System;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed record ProjectWorkspacePolicySnapshot(
    UiToolpackTier AllowedTier,
    IReadOnlyList<UiToolpackCapability> AllowedCapabilities) : IUiToolpackPolicySnapshot
{
    public static ProjectWorkspacePolicySnapshot FromWorkspace(ProjectWorkspace workspace)
    {
        if (workspace is null)
            throw new ArgumentNullException(nameof(workspace));

        var capabilities = workspace.AllowedCapabilities
            ?? ToolpackPolicyDefaults.GetAllowedCapabilities(workspace.AllowedTier);

        return new ProjectWorkspacePolicySnapshot(workspace.AllowedTier, capabilities);
    }
}
