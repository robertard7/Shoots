using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Roles;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public static class RoleCatalog
{
    public static IReadOnlyList<RoleDescriptor> GetDefaultRoles()
    {
        return new[]
        {
            new RoleDescriptor(
                "General",
                "Broad coverage across workspace needs.",
                new[]
                {
                    ToolpackCapability.FileSystem
                }),
            new RoleDescriptor(
                "Integrator",
                "Bias toward build and integration steps.",
                new[]
                {
                    ToolpackCapability.Build,
                    ToolpackCapability.Process
                }),
            new RoleDescriptor(
                "Verifier",
                "Bias toward inspection and verification steps.",
                new[]
                {
                    ToolpackCapability.FileSystem
                })
        };
    }
}
