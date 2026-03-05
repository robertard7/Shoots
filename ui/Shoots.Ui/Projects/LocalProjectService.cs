using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Projects;

public sealed class LocalProjectService
{
    private const string ProjectFileName = "project.json";
    private const string DemoPlanFileName = "demo.mmd";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _workspaceRoot;

    public LocalProjectService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Shoots.UI",
            "workspaces");
    }

    public ProjectModel CreateNewProject(string? name = null)
    {
        var createdUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid().ToString("N");
        var shortId = projectId[..8];
        var safeName = string.IsNullOrWhiteSpace(name) ? $"Project-{createdUtc:yyyyMMdd-HHmmssZ}" : name.Trim();
        var folderName = $"{createdUtc:yyyyMMdd-HHmmssZ}_{shortId}";
        var workspacePath = Path.Combine(_workspaceRoot, folderName);
        Directory.CreateDirectory(workspacePath);

        foreach (var sub in new[] { "plans", "runs", "artifacts", "notes" })
        {
            Directory.CreateDirectory(Path.Combine(workspacePath, sub));
        }

        var model = new ProjectModel(projectId, safeName, createdUtc, workspacePath, Path.Combine(workspacePath, ProjectFileName));
        File.WriteAllText(model.ProjectFilePath, JsonSerializer.Serialize(model, JsonOptions));
        return model;
    }

    public ProjectModel LoadProject(string projectFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("Project file not found.", projectFilePath);
        }

        var model = JsonSerializer.Deserialize<ProjectModel>(File.ReadAllText(projectFilePath), JsonOptions)
            ?? throw new InvalidDataException("Project file payload is invalid.");

        return model;
    }

    public string RunDemoPlan(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var plansDir = Path.Combine(project.WorkspacePath, "plans");
        var runsDir = Path.Combine(project.WorkspacePath, "runs");
        Directory.CreateDirectory(plansDir);
        Directory.CreateDirectory(runsDir);

        var planPath = Path.Combine(plansDir, DemoPlanFileName);
        var plan = "flowchart TD\n  A[Start] --> B[Write deterministic artifact]\n  B --> C[Done]\n";
        File.WriteAllText(planPath, plan);

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var runPath = Path.Combine(runsDir, runId);
        Directory.CreateDirectory(runPath);
        File.WriteAllText(Path.Combine(runPath, "result.txt"), "run complete");

        return runPath;
    }
}
