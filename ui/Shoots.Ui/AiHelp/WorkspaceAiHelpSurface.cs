using System;
using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Projects;

namespace Shoots.UI.AiHelp;

public sealed class WorkspaceAiHelpSurface : IAiHelpSurface
{
    private readonly ProjectWorkspace? _workspace;
    private readonly ToolpackTier _tier;

    public WorkspaceAiHelpSurface(ProjectWorkspace? workspace, ToolpackTier tier)
    {
        _workspace = workspace;
        _tier = tier;
    }

    public string SurfaceId => "workspace";

    public string SurfaceKind => "Workspace";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.UI),
        new AiIntentDescriptor(AiIntentType.Suggest, AiIntentScope.UI)
    };

    public string DescribeContext()
    {
        if (_workspace is null)
            return "No workspace is selected.";

        return $"Workspace '{_workspace.Name}' at {_workspace.RootPath}.";
    }

    public string DescribeCapabilities()
    {
        if (_workspace?.AllowedCapabilities is null)
            return $"Tier {_tier} with default capability set.";

        return $"Tier {_tier} with {DescribeCapabilities(_workspace.AllowedCapabilities)}.";
    }

    public string DescribeConstraints()
        => "Workspace context is read-only in this UI.";

    private static string DescribeCapabilities(IReadOnlyList<ToolpackCapability> capabilities)
    {
        if (capabilities.Count == 0)
            return "no additional capabilities";

        return string.Join(", ", capabilities);
    }
}
