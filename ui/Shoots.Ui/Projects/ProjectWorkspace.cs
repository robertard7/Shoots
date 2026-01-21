namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
// ProjectWorkspace is pure data and must not reference runtime types.
public sealed record ProjectWorkspace(
    string Name,
    string RootPath,
    DateTimeOffset LastOpenedUtc
);
