using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public static class ToolpackPolicyDefaults
{
    public static IReadOnlyList<ToolpackCapability> GetAllowedCapabilities(ToolpackTier tier)
    {
        return tier switch
        {
            ToolpackTier.Public => new[]
            {
                ToolpackCapability.FileSystem
            },
            ToolpackTier.Developer => new[]
            {
                ToolpackCapability.FileSystem,
                ToolpackCapability.Build,
                ToolpackCapability.Process
            },
            ToolpackTier.System => new[]
            {
                ToolpackCapability.FileSystem,
                ToolpackCapability.Build,
                ToolpackCapability.Process,
                ToolpackCapability.Network,
                ToolpackCapability.Kernel,
                ToolpackCapability.Deploy
            },
            _ => new[]
            {
                ToolpackCapability.FileSystem
            }
        };
    }
}
