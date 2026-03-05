using System;
using System.Collections.Generic;
using System.IO;

namespace Shoots.UI.Builder;

public sealed class ToolExecutionService
{
    public string ExecuteStep(PlanStep step, string workspacePath)
    {
        return step.ToolId switch
        {
            "write_text" => ExecuteWriteText(step, workspacePath),
            "create_directory" => ExecuteCreateDirectory(step, workspacePath),
            "copy_file" => ExecuteCopyFile(step, workspacePath),
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

    private static string ResolvePath(string workspacePath, string relativeOrAbsolute)
        => Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(workspacePath, relativeOrAbsolute);

    private static string GetArg(IReadOnlyDictionary<string, string> args, string key, string fallback)
        => args.TryGetValue(key, out var value) ? value : fallback;
}
