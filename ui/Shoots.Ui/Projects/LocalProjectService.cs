using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Shoots.UI.Projects;

public sealed class LocalProjectService
{
    private const string ProjectFileName = "project.json";
    private const string DemoPlanFileName = "demo.mmd";
    private const string RunCounterFileName = ".run-counter";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] RequiredFolders = { "plans", "runs", "artifacts", "notes" };

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
        var safeName = NormalizeProjectName(name, createdUtc);
        var finalFolderName = $"{createdUtc:yyyyMMdd-HHmmssZ}_{shortId}";
        var tempWorkspacePath = Path.Combine(_workspaceRoot, ".tmp", projectId);
        var finalWorkspacePath = Path.Combine(_workspaceRoot, finalFolderName);

        EnsureValidWorkspacePath(finalWorkspacePath);

        Directory.CreateDirectory(Path.GetDirectoryName(tempWorkspacePath)!);
        Directory.CreateDirectory(_workspaceRoot);

        try
        {
            if (Directory.Exists(tempWorkspacePath))
            {
                Directory.Delete(tempWorkspacePath, recursive: true);
            }

            Directory.CreateDirectory(tempWorkspacePath);
            CreateRequiredFolders(tempWorkspacePath);

            var tempProjectFilePath = Path.Combine(tempWorkspacePath, ProjectFileName);
            var model = new ProjectModel(projectId, safeName, createdUtc, finalWorkspacePath, Path.Combine(finalWorkspacePath, ProjectFileName));
            File.WriteAllText(tempProjectFilePath, JsonSerializer.Serialize(model, JsonOptions));

            if (Directory.Exists(finalWorkspacePath))
            {
                throw new IOException($"Workspace path already exists: {finalWorkspacePath}");
            }

            Directory.Move(tempWorkspacePath, finalWorkspacePath);
            return model;
        }
        catch
        {
            if (Directory.Exists(tempWorkspacePath))
            {
                Directory.Delete(tempWorkspacePath, recursive: true);
            }

            throw;
        }
    }

    public ProjectModel LoadProject(string projectFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("Project file not found.", projectFilePath);
        }

        var model = JsonSerializer.Deserialize<ProjectModel>(File.ReadAllText(projectFilePath), JsonOptions)
            ?? throw new InvalidDataException("Project file payload is invalid.");

        if (string.IsNullOrWhiteSpace(model.ProjectId) || string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.WorkspacePath))
        {
            throw new InvalidDataException("Project file payload is missing required fields.");
        }

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

        var runId = NextRunId(project.WorkspacePath);
        var runPath = Path.Combine(runsDir, runId);
        Directory.CreateDirectory(runPath);
        File.WriteAllText(Path.Combine(runPath, "result.txt"), "run complete");

        return runPath;
    }

    public IReadOnlyList<string> VerifyProjectStructure(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var errors = new List<string>();
        if (!Directory.Exists(project.WorkspacePath))
        {
            errors.Add($"workspace missing: {project.WorkspacePath}");
            return errors;
        }

        if (!File.Exists(project.ProjectFilePath))
        {
            errors.Add($"project file missing: {project.ProjectFilePath}");
        }

        foreach (var required in RequiredFolders)
        {
            var path = Path.Combine(project.WorkspacePath, required);
            if (!Directory.Exists(path))
            {
                errors.Add($"required folder missing: {path}");
            }
        }

        return errors;
    }

    private static void CreateRequiredFolders(string workspacePath)
    {
        foreach (var required in RequiredFolders)
        {
            Directory.CreateDirectory(Path.Combine(workspacePath, required));
        }
    }

    private static string NormalizeProjectName(string? name, DateTimeOffset createdUtc)
    {
        var value = string.IsNullOrWhiteSpace(name) ? $"Project-{createdUtc:yyyyMMdd-HHmmssZ}" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return value;
    }

    private static void EnsureValidWorkspacePath(string path)
    {
        if (path.Length > 240)
        {
            throw new PathTooLongException($"Workspace path exceeds supported length: {path.Length}");
        }
    }

    private static string NextRunId(string workspacePath)
    {
        var counterPath = Path.Combine(workspacePath, RunCounterFileName);
        var current = 0;
        if (File.Exists(counterPath) && int.TryParse(File.ReadAllText(counterPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            current = parsed;
        }

        var next = current + 1;
        File.WriteAllText(counterPath, next.ToString(CultureInfo.InvariantCulture));
        return next.ToString("D6", CultureInfo.InvariantCulture);
    }
}
