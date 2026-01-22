namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
// ProjectWorkspace is pure data and should not reference runtime types.
public sealed record ProjectWorkspace(
    string Name,
    string RootPath,
    DateTimeOffset LastOpenedUtc,
    Shoots.Runtime.Ui.Abstractions.ToolpackTier AllowedTier = Shoots.Runtime.Ui.Abstractions.ToolpackTier.Public,
    IReadOnlyList<Shoots.Runtime.Ui.Abstractions.ToolpackCapability>? AllowedCapabilities = null,
    string? RootFsId = null
);
