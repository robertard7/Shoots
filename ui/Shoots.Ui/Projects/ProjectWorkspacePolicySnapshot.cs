using System;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed record ProjectWorkspacePolicySnapshot(
    ToolpackTier AllowedTier,
    IReadOnlyList<ToolpackCapability> AllowedCapabilities) : IToolpackPolicySnapshot
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
