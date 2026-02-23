using System.Diagnostics;
using System.IO.Compression;
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
            new LinuxArchiveUnzipHandler(),
            new LinuxArchiveZipHandler(),
            new LinuxFsAppendTextHandler(),
            new LinuxFsCopyHandler(),
            new LinuxFsLsHandler(),
            new LinuxFsMkdirHandler(),
            new LinuxFsMoveHandler(),
            new LinuxFsReadTextHandler(),
            new LinuxFsRmHandler(),
            new LinuxFsStatHandler(),
            new LinuxFsWriteTextHandler(),
            new LinuxGitCloneHandler(),
            new LinuxGitStatusHandler(),
            new LinuxHttpGetTextHandler(),
            new LinuxProcExecHandler(),
            new LinuxTextReplaceHandler()
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

    public static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
            return value;

        return Encoding.UTF8.GetString(bytes, 0, maxBytes);
    }
}

internal static class ToolPath
{
    public static string ResolveWithinRoot(ToolExecutionContext ctx, string relativeOrRooted)
    {
        var candidate = Path.GetFullPath(Path.IsPathRooted(relativeOrRooted)
            ? relativeOrRooted
            : Path.Combine(ctx.RepoRoot, relativeOrRooted));

        var normalizedRoot = Path.GetFullPath(ctx.RepoRoot).TrimEnd(Path.DirectorySeparatorChar);
        var rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, normalizedRoot, StringComparison.Ordinal)
            && !candidate.StartsWith(rootWithSlash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("path escapes repo root");
        }

        return candidate;
    }

    public static string ToRepoRelative(ToolExecutionContext ctx, string fullPath)
    {
        var rel = Path.GetRelativePath(ctx.RepoRoot, fullPath);
        return rel.Replace('\\', '/');
    }
}

public sealed class LinuxFsMkdirHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.mkdir.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["path"] = ToolPath.ToRepoRelative(ctx, path),
                ["created"] = !existed
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.mkdir_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsWriteTextHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.write_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var relPath = Convert.ToString(invocation.Bindings["path"]) ?? string.Empty;
            var text = Convert.ToString(invocation.Bindings["text"]) ?? string.Empty;
            var overwrite = invocation.Bindings.TryGetValue("overwrite", out var value) ? Convert.ToBoolean(value) : true;
            var encodingName = invocation.Bindings.TryGetValue("encoding", out var encObj) ? Convert.ToString(encObj) : "utf-8";
            var encoding = Encoding.GetEncoding(encodingName ?? "utf-8");
            var path = ToolPath.ResolveWithinRoot(ctx, relPath);

            if (!overwrite && File.Exists(path))
                return ToolResultFactory.Error(Id, "fs.exists", "File already exists and overwrite=false.");

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.RepoRoot);
            File.WriteAllText(path, text, encoding);

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["path"] = ToolPath.ToRepoRelative(ctx, path),
                ["bytes_written"] = encoding.GetByteCount(text)
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsAppendTextHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.append_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var text = Convert.ToString(invocation.Bindings["text"]) ?? string.Empty;
            var newline = invocation.Bindings.TryGetValue("newline", out var nlObj) ? Convert.ToBoolean(nlObj) : true;
            var content = newline ? text + Environment.NewLine : text;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.RepoRoot);
            File.AppendAllText(path, content, Encoding.UTF8);

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["bytes_appended"] = Encoding.UTF8.GetByteCount(content)
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.append_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsReadTextHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.read_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var relPath = Convert.ToString(invocation.Bindings["path"]) ?? string.Empty;
            var encodingName = invocation.Bindings.TryGetValue("encoding", out var encObj) ? Convert.ToString(encObj) : "utf-8";
            var maxBytes = invocation.Bindings.TryGetValue("max_bytes", out var maxObj) ? Convert.ToInt32(maxObj) : ctx.MaxBytesOut;
            var path = ToolPath.ResolveWithinRoot(ctx, relPath);
            var bytes = File.ReadAllBytes(path);
            var truncated = bytes.Length > maxBytes;
            var selected = truncated ? bytes.Take(Math.Max(0, maxBytes)).ToArray() : bytes;
            var encoding = Encoding.GetEncoding(encodingName ?? "utf-8");

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["path"] = ToolPath.ToRepoRelative(ctx, path),
                ["text"] = encoding.GetString(selected),
                ["truncated"] = truncated,
                ["bytes_read"] = bytes.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.read_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsRmHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.rm.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var recursive = invocation.Bindings.TryGetValue("recursive", out var r) && Convert.ToBoolean(r);
            var force = invocation.Bindings.TryGetValue("force", out var f) && Convert.ToBoolean(f);

            if (File.Exists(path))
            {
                File.Delete(path);
                return new ToolResult(Id, new Dictionary<string, object?> { ["removed"] = true, ["path"] = ToolPath.ToRepoRelative(ctx, path) }, true);
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
                return new ToolResult(Id, new Dictionary<string, object?> { ["removed"] = true, ["path"] = ToolPath.ToRepoRelative(ctx, path) }, true);
            }

            if (force)
                return new ToolResult(Id, new Dictionary<string, object?> { ["removed"] = false, ["path"] = ToolPath.ToRepoRelative(ctx, path) }, true);

            return ToolResultFactory.Error(Id, "fs.not_found", "Path does not exist.");
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.rm_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsLsHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.ls.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var recursive = invocation.Bindings.TryGetValue("recursive", out var r) && Convert.ToBoolean(r);
            var includeHidden = invocation.Bindings.TryGetValue("include_hidden", out var h) && Convert.ToBoolean(h);
            var maxEntries = invocation.Bindings.TryGetValue("max_entries", out var m) ? Math.Max(1, Convert.ToInt32(m)) : 200;

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = Directory.EnumerateFileSystemEntries(path, "*", option)
                .Select(p => ToolPath.ToRepoRelative(ctx, p))
                .Where(rel => includeHidden || !Path.GetFileName(rel).StartsWith('.', StringComparison.Ordinal))
                .OrderBy(static p => p, StringComparer.Ordinal)
                .Take(maxEntries)
                .ToArray();

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["entries"] = string.Join("\n", entries),
                ["count"] = entries.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.ls_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsStatHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.stat.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return new ToolResult(Id, new Dictionary<string, object?>
                {
                    ["exists"] = false,
                    ["kind"] = "other",
                    ["size_bytes"] = 0L,
                    ["mtime_utc"] = "1970-01-01T00:00:00Z",
                    ["error.code"] = "fs.not_found"
                }, true);
            }

            var attrs = File.GetAttributes(path);
            var isDir = attrs.HasFlag(FileAttributes.Directory);
            var isSymlink = attrs.HasFlag(FileAttributes.ReparsePoint);
            var kind = isSymlink ? "symlink" : isDir ? "dir" : "file";
            long size = 0;
            DateTimeOffset mtime;
            if (isDir)
            {
                mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path));
            }
            else
            {
                var info = new FileInfo(path);
                size = info.Length;
                mtime = new DateTimeOffset(info.LastWriteTimeUtc);
            }

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["exists"] = true,
                ["kind"] = kind,
                ["size_bytes"] = size,
                ["mtime_utc"] = mtime.ToUniversalTime().ToString("O")
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.stat_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsMoveHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.move.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var from = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["from"]) ?? string.Empty);
            var to = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["to"]) ?? string.Empty);
            Directory.CreateDirectory(Path.GetDirectoryName(to) ?? ctx.RepoRoot);

            if (File.Exists(from))
                File.Move(from, to, overwrite: true);
            else if (Directory.Exists(from))
                Directory.Move(from, to);
            else
                return ToolResultFactory.Error(Id, "fs.not_found", "Source path does not exist.");

            return new ToolResult(Id, new Dictionary<string, object?> { ["moved"] = true }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.move_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsCopyHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.copy.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var from = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["from"]) ?? string.Empty);
            var to = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["to"]) ?? string.Empty);
            var overwrite = invocation.Bindings.TryGetValue("overwrite", out var o) && Convert.ToBoolean(o);

            if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to) ?? ctx.RepoRoot);
                File.Copy(from, to, overwrite);
            }
            else if (Directory.Exists(from))
            {
                CopyDirectory(from, to, overwrite);
            }
            else
            {
                return ToolResultFactory.Error(Id, "fs.not_found", "Source path does not exist.");
            }

            return new ToolResult(Id, new Dictionary<string, object?> { ["copied"] = true }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.copy_failed", ex.Message);
        }
    }

    private static void CopyDirectory(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(static x => x, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite);
        }
    }
}

public sealed class LinuxTextReplaceHandler : IToolHandler
{
    public ToolId Id => new("linux.text.replace.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var search = Convert.ToString(invocation.Bindings["search"]) ?? string.Empty;
            var replace = Convert.ToString(invocation.Bindings["replace"]) ?? string.Empty;
            var count = invocation.Bindings.TryGetValue("count", out var c) ? Convert.ToInt32(c) : 0;

            var text = File.ReadAllText(path, Encoding.UTF8);
            var replacements = 0;
            string updated;
            if (count <= 0)
            {
                replacements = CountOccurrences(text, search);
                updated = text.Replace(search, replace, StringComparison.Ordinal);
            }
            else
            {
                updated = ReplaceCount(text, search, replace, count, out replacements);
            }

            var written = !string.Equals(updated, text, StringComparison.Ordinal);
            if (written)
                File.WriteAllText(path, updated, Encoding.UTF8);

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["replacements"] = replacements,
                ["written"] = written
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "text.replace_failed", ex.Message);
        }
    }

    private static int CountOccurrences(string text, string search)
    {
        if (search.Length == 0)
            return 0;

        var count = 0;
        var start = 0;
        while (true)
        {
            var idx = text.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            count++;
            start = idx + search.Length;
        }

        return count;
    }

    private static string ReplaceCount(string text, string search, string replace, int count, out int replaced)
    {
        replaced = 0;
        if (search.Length == 0)
            return text;

        var builder = new StringBuilder();
        var start = 0;
        while (replaced < count)
        {
            var idx = text.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            builder.Append(text, start, idx - start);
            builder.Append(replace);
            start = idx + search.Length;
            replaced++;
        }

        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }
}

public sealed class LinuxArchiveZipHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.zip.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var sourceDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["source_dir"]) ?? string.Empty);
            var zipPath = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["zip_path"]) ?? string.Empty);
            var includeHidden = invocation.Bindings.TryGetValue("include_hidden", out var h) && Convert.ToBoolean(h);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? ctx.RepoRoot);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                .OrderBy(static f => f, StringComparer.Ordinal)
                .Where(file => includeHidden || !Path.GetFileName(file).StartsWith('.', StringComparison.Ordinal))
                .ToArray();

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, rel);
            }

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["archived"] = true,
                ["files_count"] = files.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "archive.zip_failed", ex.Message);
        }
    }
}

public sealed class LinuxArchiveUnzipHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.unzip.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var zipPath = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["zip_path"]) ?? string.Empty);
            var destDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["dest_dir"]) ?? string.Empty);
            var overwrite = invocation.Bindings.TryGetValue("overwrite", out var o) && Convert.ToBoolean(o);
            Directory.CreateDirectory(destDir);

            var filesCount = 0;
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries.OrderBy(static e => e.FullName, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var targetPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                var normalizedDest = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!targetPath.StartsWith(normalizedDest, StringComparison.Ordinal))
                    return ToolResultFactory.Error(Id, "archive.zip_slip", "Archive entry escapes destination directory.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite);
                filesCount++;
            }

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["extracted"] = true,
                ["files_count"] = filesCount
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "archive.unzip_failed", ex.Message);
        }
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
            var cwd = string.IsNullOrWhiteSpace(cwdBinding) ? ctx.WorkingDirectory : ToolPath.ResolveWithinRoot(ctx, cwdBinding);
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
                ["stdout"] = ToolResultFactory.TruncateUtf8(stdout, maxOutputBytes),
                ["stderr"] = ToolResultFactory.TruncateUtf8(stderr, maxOutputBytes),
                ["timed_out"] = !completed
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "proc.exec_failed", ex.Message);
        }
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
                ["args"] = new object?[] { "-C", ToolPath.ResolveWithinRoot(ctx, repoPath), "status", "--porcelain" }
            },
            invocation.WorkOrderId), ctx);

        if (!result.Success)
            return ToolResultFactory.Error(Id, "git.status_failed", "Unable to run git status.");

        var porcelain = Convert.ToString(result.Outputs["stdout"]) ?? string.Empty;
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["porcelain"] = ToolResultFactory.TruncateUtf8(porcelain, ctx.MaxBytesOut),
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
        var destinationPath = ToolPath.ResolveWithinRoot(ctx, dest);

        var args = new List<object?> { "clone" };
        if (depth > 0)
        {
            args.Add("--depth");
            args.Add(depth.ToString());
        }

        args.Add(url);
        args.Add(destinationPath);

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
            ["dest_path"] = ToolPath.ToRepoRelative(ctx, destinationPath),
            ["cloned"] = Convert.ToInt32(result.Outputs["exit_code"]) == 0
        }, true);
    }
}

public sealed class LinuxHttpGetTextHandler : IToolHandler
{
    public ToolId Id => new("linux.net.http_get_text.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        if (!ctx.AllowNetwork)
            return ToolResultFactory.Error(Id, "network_disabled", "Network access is disabled for tool execution context.");

        try
        {
            var url = Convert.ToString(invocation.Bindings["url"]) ?? string.Empty;
            var timeoutMs = invocation.Bindings.TryGetValue("timeout_ms", out var timeoutObj) ? Convert.ToInt32(timeoutObj) : 5000;
            var maxBytes = invocation.Bindings.TryGetValue("max_bytes", out var maxObj) ? Convert.ToInt32(maxObj) : ctx.MaxBytesOut;
            var allowRedirects = invocation.Bindings.TryGetValue("allow_redirects", out var r) && Convert.ToBoolean(r);

            using var handler = new HttpClientHandler { AllowAutoRedirect = allowRedirects };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 30000))
            };

            using var response = client.GetAsync(url, ctx.CancellationToken).GetAwaiter().GetResult();
            var bytes = response.Content.ReadAsByteArrayAsync(ctx.CancellationToken).GetAwaiter().GetResult();
            var text = Encoding.UTF8.GetString(bytes.Take(Math.Max(0, maxBytes)).ToArray());

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["status_code"] = (int)response.StatusCode,
                ["text"] = text
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "network.http_get_failed", ex.Message);
        }
    }
}
