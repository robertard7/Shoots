using System.Collections.Generic;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public static class ToolpackPolicyDefaults
{
    public static IReadOnlyList<UiToolpackCapability> GetAllowedCapabilities(UiToolpackTier tier)
    {
        return tier switch
        {
            UiToolpackTier.Public => new[]
            {
                UiToolpackCapability.FileSystem
            },
            UiToolpackTier.Developer => new[]
            {
                UiToolpackCapability.FileSystem,
                UiToolpackCapability.Build,
                UiToolpackCapability.Process
            },
            UiToolpackTier.System => new[]
            {
                UiToolpackCapability.FileSystem,
                UiToolpackCapability.Build,
                UiToolpackCapability.Process,
                UiToolpackCapability.Network,
                UiToolpackCapability.Kernel,
                UiToolpackCapability.Deploy
            },
            _ => new[]
            {
                UiToolpackCapability.FileSystem
            }
        };
    }
}
