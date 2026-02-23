using System.Diagnostics;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Tools.Abstractions;

namespace Shoots.Tools.Linux;

public sealed class LinuxToolHandlerRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers;

    public LinuxToolHandlerRegistry(IEnumerable<IToolHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Id.Value, StringComparer.Ordinal);
    }

    public IToolHandler? Resolve(ToolId id)
        => _handlers.TryGetValue(id.Value, out var handler) ? handler : null;

    public static LinuxToolHandlerRegistry CreateDefault()
        => new(new IToolHandler[]
        {
            new LinuxFsMkdirHandler(),
            new LinuxFsWriteTextHandler(),
            new LinuxFsReadTextHandler(),
            new LinuxProcExecHandler(),
            new LinuxGitStatusHandler(),
            new LinuxGitCloneHandler()
        });
}

internal static class ToolResultFactory
{
    public static ToolResult Error(ToolId toolId, string code, string message)
        => new(toolId, new Dictionary<string, object?>
        {
            ["error.code"] = code,
            ["error.message"] = message
        }, false);
}

public sealed class LinuxFsMkdirHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.mkdir.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var path = Path.Combine(ctx.RepoRoot, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
        var recursive = invocation.Bindings.TryGetValue("recursive", out var value) ? Convert.ToBoolean(value) : true;

        if (Directory.Exists(path))
            return new ToolResult(Id, new Dictionary<string, object?> { ["path"] = path, ["created"] = false }, true);

        if (recursive)
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path);

        return new ToolResult(Id, new Dictionary<string, object?> { ["path"] = path, ["created"] = true }, true);
    }
}

public sealed class LinuxFsWriteTextHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.write_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var relPath = Convert.ToString(invocation.Bindings["path"]) ?? string.Empty;
        var text = Convert.ToString(invocation.Bindings["text"]) ?? string.Empty;
        var overwrite = invocation.Bindings.TryGetValue("overwrite", out var value) ? Convert.ToBoolean(value) : true;
        var encodingName = invocation.Bindings.TryGetValue("encoding", out var encObj) ? Convert.ToString(encObj) : "utf-8";
        var encoding = Encoding.GetEncoding(encodingName ?? "utf-8");
        var path = Path.Combine(ctx.RepoRoot, relPath);

        if (!overwrite && File.Exists(path))
            return ToolResultFactory.Error(Id, "fs.exists", "File already exists and overwrite=false.");

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.RepoRoot);
        File.WriteAllText(path, text, encoding);
        var bytes = encoding.GetByteCount(text);

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["bytes_written"] = bytes
        }, true);
    }
}

public sealed class LinuxFsReadTextHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.read_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var relPath = Convert.ToString(invocation.Bindings["path"]) ?? string.Empty;
        var encodingName = invocation.Bindings.TryGetValue("encoding", out var encObj) ? Convert.ToString(encObj) : "utf-8";
        var maxBytes = invocation.Bindings.TryGetValue("max_bytes", out var maxObj) ? Convert.ToInt32(maxObj) : ctx.MaxBytesOut;
        var path = Path.Combine(ctx.RepoRoot, relPath);
        var bytes = File.ReadAllBytes(path);
        var truncated = bytes.Length > maxBytes;
        var selected = truncated ? bytes.Take(maxBytes).ToArray() : bytes;
        var encoding = Encoding.GetEncoding(encodingName ?? "utf-8");

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["text"] = encoding.GetString(selected),
            ["truncated"] = truncated,
            ["bytes_read"] = bytes.Length
        }, true);
    }
}

public sealed class LinuxProcExecHandler : IToolHandler
{
    public ToolId Id => new("linux.proc.exec.v1");
    private const int MaxTimeoutMs = 30000;

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var file = Convert.ToString(invocation.Bindings["file"]);
            var args = invocation.Bindings.TryGetValue("args", out var argsObj) && argsObj is IEnumerable<object?> list
                ? list.Select(static a => Convert.ToString(a) ?? string.Empty).ToArray()
                : Array.Empty<string>();
            var cwdBinding = invocation.Bindings.TryGetValue("cwd", out var cwdObj) ? Convert.ToString(cwdObj) : null;
            var cwd = string.IsNullOrWhiteSpace(cwdBinding) ? ctx.WorkingDirectory : Path.Combine(ctx.RepoRoot, cwdBinding);
            var timeoutMs = Math.Min(invocation.Bindings.TryGetValue("timeout_ms", out var timeoutObj) ? Convert.ToInt32(timeoutObj) : 5000, MaxTimeoutMs);
            var maxOutputBytes = Math.Min(invocation.Bindings.TryGetValue("max_output_bytes", out var maxObj) ? Convert.ToInt32(maxObj) : ctx.MaxBytesOut, ctx.MaxBytesOut);

            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var completed = process.WaitForExit(timeoutMs);
            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["exit_code"] = completed ? process.ExitCode : -1,
                ["stdout"] = Truncate(stdout, maxOutputBytes),
                ["stderr"] = Truncate(stderr, maxOutputBytes),
                ["timed_out"] = !completed
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "proc.exec_failed", ex.Message);
        }
    }

    private static string Truncate(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
            return value;

        return Encoding.UTF8.GetString(bytes, 0, maxBytes);
    }
}

public sealed class LinuxGitStatusHandler : IToolHandler
{
    public ToolId Id => new("linux.git.status.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var repoPath = Convert.ToString(invocation.Bindings["repo_path"]) ?? ".";
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(
            proc.Id,
            new Dictionary<string, object?>
            {
                ["file"] = "git",
                ["args"] = new object?[] { "-C", Path.Combine(ctx.RepoRoot, repoPath), "status", "--porcelain" }
            },
            invocation.WorkOrderId), ctx);

        if (!result.Success)
            return ToolResultFactory.Error(Id, "git.status_failed", "Unable to run git status.");

        var porcelain = Convert.ToString(result.Outputs["stdout"]) ?? string.Empty;
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["porcelain"] = porcelain,
            ["clean"] = string.IsNullOrWhiteSpace(porcelain)
        }, true);
    }
}

public sealed class LinuxGitCloneHandler : IToolHandler
{
    public ToolId Id => new("linux.git.clone.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var url = Convert.ToString(invocation.Bindings["url"]) ?? string.Empty;
        var dest = Convert.ToString(invocation.Bindings["dest_path"]) ?? string.Empty;
        var depth = invocation.Bindings.TryGetValue("depth", out var depthObj) ? Convert.ToInt32(depthObj) : 0;

        var args = new List<object?> { "clone" };
        if (depth > 0)
        {
            args.Add("--depth");
            args.Add(depth.ToString());
        }

        args.Add(url);
        args.Add(Path.Combine(ctx.RepoRoot, dest));

        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(
            proc.Id,
            new Dictionary<string, object?>
            {
                ["file"] = "git",
                ["args"] = args.ToArray(),
                ["timeout_ms"] = 20000
            },
            invocation.WorkOrderId), ctx);

        if (!result.Success || Convert.ToBoolean(result.Outputs["timed_out"]))
            return ToolResultFactory.Error(Id, "git.clone_failed", "Unable to clone repository.");

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["dest_path"] = Path.Combine(ctx.RepoRoot, dest),
            ["cloned"] = Convert.ToInt32(result.Outputs["exit_code"]) == 0
        }, true);
    }
}
