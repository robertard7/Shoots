namespace Shoots.Runtime.Ui.Abstractions;

public sealed record RoleDescriptor(
    string Name,
    string Description,
    IReadOnlyList<ToolpackCapability> PreferredCapabilities
);
