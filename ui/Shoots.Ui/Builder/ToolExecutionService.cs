using System;
using System.Collections.Generic;
using System.IO;

namespace Shoots.UI.Builder;

public sealed class ToolExecutionService
{
    private readonly ToolRegistry _toolRegistry;

    public ToolExecutionService(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    public string ExecuteStep(PlanStep step, string workspacePath)
    {
        var definition = _toolRegistry.Get(step.ToolId);
        ValidateRequiredArgs(definition, step.Args);

        return step.ToolId switch
        {
            "write_text" => ExecuteWriteText(step, workspacePath),
            "create_directory" => ExecuteCreateDirectory(step, workspacePath),
            "copy_file" => ExecuteCopyFile(step, workspacePath),
            "git.clone" => ExecuteGitClone(step, workspacePath),
            "dotnet.build" => ExecuteDotnetBuild(step, workspacePath),
            "cmake.configure" => ExecuteCmakeConfigure(step, workspacePath),
            "cmake.build" => ExecuteCmakeBuild(step, workspacePath),
            _ => throw new InvalidOperationException($"Unknown tool id: {step.ToolId}")
        };
    }

    private static string ExecuteWriteText(PlanStep step, string workspacePath)
    {
        var path = ResolvePath(workspacePath, GetArg(step.Args, "path", step.OutputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, GetArg(step.Args, "text", string.Empty));
        return path;
    }

    private static string ExecuteCreateDirectory(PlanStep step, string workspacePath)
    {
        var path = ResolvePath(workspacePath, GetArg(step.Args, "path", step.OutputPath));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExecuteCopyFile(PlanStep step, string workspacePath)
    {
        var source = ResolvePath(workspacePath, GetArg(step.Args, "source", string.Empty));
        var destination = ResolvePath(workspacePath, GetArg(step.Args, "destination", step.OutputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private static string ExecuteGitClone(PlanStep step, string workspacePath)
    {
        var destination = ResolvePath(workspacePath, GetArg(step.Args, "destination", step.OutputPath));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "clone.log"), $"repo={GetArg(step.Args, "repo", "")}");
        return destination;
    }

    private static string ExecuteDotnetBuild(PlanStep step, string workspacePath)
    {
        var logPath = ResolvePath(workspacePath, step.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, $"dotnet build project={GetArg(step.Args, "project", "")}");
        return logPath;
    }

    private static string ExecuteCmakeConfigure(PlanStep step, string workspacePath)
    {
        var logPath = ResolvePath(workspacePath, step.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, $"cmake configure source={GetArg(step.Args, "source", "")} buildDir={GetArg(step.Args, "buildDir", "")}");
        return logPath;
    }

    private static string ExecuteCmakeBuild(PlanStep step, string workspacePath)
    {
        var logPath = ResolvePath(workspacePath, step.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, $"cmake build buildDir={GetArg(step.Args, "buildDir", "")}");
        return logPath;
    }

    private static void ValidateRequiredArgs(ToolDefinition definition, IReadOnlyDictionary<string, string> args)
    {
        foreach (var required in definition.RequiredArgs)
        {
            if (!args.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Tool '{definition.Id}' missing required arg '{required}'");
            }
        }
    }

    private static string ResolvePath(string workspacePath, string relativeOrAbsolute)
        => SafePath.ResolveUnderWorkspace(workspacePath, relativeOrAbsolute);

    private static string GetArg(IReadOnlyDictionary<string, string> args, string key, string fallback)
        => args.TryGetValue(key, out var value) ? value : fallback;
}
