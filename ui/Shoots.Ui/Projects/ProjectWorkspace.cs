namespace Shoots.UI.Projects;

public sealed record ProjectWorkspace(
    string Name,
    string RootPath,
    DateTimeOffset LastOpenedUtc
);
