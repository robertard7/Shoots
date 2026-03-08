namespace Shoots.UI.Projects;

public sealed record ProjectModel(
    string ProjectId,
    string Name,
    DateTimeOffset CreatedUtc,
    string WorkspacePath,
    string ProjectFilePath
);
