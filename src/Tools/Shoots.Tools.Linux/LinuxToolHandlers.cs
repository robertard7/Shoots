using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Tools.Abstractions;

namespace Shoots.Tools.Linux;

public sealed class LinuxToolHandlerRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers;

    public LinuxToolHandlerRegistry(IEnumerable<IToolHandler> handlers)
    {
        var ordered = handlers.OrderBy(h => h.Id.Value, StringComparer.Ordinal).ToArray();
        var duplicate = ordered
            .GroupBy(h => h.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"duplicate tool handler id: {duplicate.Key}");
        }

        _handlers = ordered.ToDictionary(h => h.Id.Value, StringComparer.Ordinal);
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
            new LinuxFsTouchHandler(),
            new LinuxFsEnsureFileHandler(),
            new LinuxFsChmodHandler(),
            new LinuxFsSymlinkHandler(),
            new LinuxFsReadlinkHandler(),
            new LinuxFsRealpathHandler(),
            new LinuxGitAddHandler(),
            new LinuxGitCheckoutHandler(),
            new LinuxGitCloneHandler(),
            new LinuxGitCommitHandler(),
            new LinuxGitDiffNamesHandler(),
            new LinuxGitLogHandler(),
            new LinuxGitRevParseHandler(),
            new LinuxGitStatusHandler(),
            new LinuxBuildCMakeBuildHandler(),
            new LinuxBuildCMakeConfigureHandler(),
            new LinuxBuildDotnetBuildHandler(),
            new LinuxBuildDotnetTestHandler(),
            new LinuxEnvGetHandler(),
            new LinuxEnvSetLocalHandler(),
            new LinuxHttpGetTextHandler(),
            new LinuxProcExecHandler(),
            new LinuxProcWhichHandler(),
            new LinuxTextReplaceHandler(),
            new LinuxTextApplyUnifiedDiffHandler(),
            new LinuxTextExtractBetweenMarkersHandler(),
            new LinuxTextInsertAfterMarkerHandler(),
            new LinuxTextLineEndingNormalizeHandler(),
            new LinuxGitInitHandler(),
            new LinuxGitBranchListHandler(),
            new LinuxGitMergeHandler(),
            new LinuxGitResetHardHandler(),
            new LinuxGitCleanFdHandler(),
            new LinuxFsEnsureDirHandler(),
            new LinuxFsExistsHandler(),
            new LinuxFsFindFilesHandler(),
            new LinuxFsGlobHandler(),
            new LinuxTextRgHandler(),
            new LinuxTextReplaceInFilesHandler(),
            new LinuxBuildDotnetRestoreHandler(),
            new LinuxBuildDotnetPackHandler(),
            new LinuxBuildCMakeCTestHandler(),
            new LinuxArchiveTarGzHandler(),
            new LinuxArchiveUntarGzHandler(),
            new LinuxArtifactsHashTreeHandler(),
            new LinuxGitFetchHandler(),
            new LinuxGitPullFfOnlyHandler(),
            new LinuxGitMergeAbortHandler(),
            new LinuxGitRebaseAbortHandler(),
            new LinuxGitStatusPorcelainHandler(),
            new LinuxGitCurrentBranchHandler(),
            new LinuxGitRemoteListHandler(),
            new LinuxFsCwdHandler(),
            new LinuxFsPathJoinHandler(),
            new LinuxFsTempDirHandler(),
            new LinuxTextTrimHandler(),
            new LinuxTextToJsonHandler(),
            new LinuxTextFromJsonHandler(),
            new LinuxBuildDotnetSlnListProjectsHandler(),
            new LinuxBuildDotnetListPackagesHandler(),
            new LinuxProcPsHandler(),
            new LinuxArchiveZipDirHandler(),
            new LinuxArchiveUnzipToDirHandler(),
            new LinuxGitTagAnnotatedHandler(),
            new LinuxGitPushTagHandler(),
            new LinuxGitFetchAllHandler(),
            new LinuxGitPullRebaseFfOnlyHandler(),
            new LinuxGitCloneDepthHandler(),
            new LinuxGitSubmoduleUpdateHandler(),
            new LinuxGitRemoteSetUrlHandler(),
            new LinuxGitCommitAmendHandler(),
            new LinuxGitBranchCreateHandler(),
            new LinuxGitBranchDeleteHandler(),
            new LinuxGitTagListHandler(),
            new LinuxGitDescribeHandler(),
            new LinuxGitChangedFilesHandler(),
            new LinuxGitIsCleanHandler(),
            new LinuxBuildDotnetCleanHandler(),
            new LinuxBuildDotnetPublishHandler(),
            new LinuxBuildDotnetFormatVerifyHandler(),
            new LinuxBuildCMakeInstallHandler(),
            new LinuxSysDiskUsageHandler(),
            new LinuxSysMemInfoHandler(),
            new LinuxSysUnameHandler(),
            new LinuxSysCpuinfoHandler(),
            new LinuxSysDiskFreeHandler(),
            new LinuxSysEnvDumpSafeHandler(),
            new LinuxHashFileSha256Handler(),
            new LinuxHashDirManifestHandler()
        });
}

internal static class LinuxTextPatchUtil
{
    public static string StripPrefix(string path, int strip)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
            normalized = normalized[2..];
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (strip <= 0) return string.Join('/', parts);
        return strip >= parts.Length ? string.Empty : string.Join('/', parts.Skip(strip));
    }
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


public sealed class LinuxTextApplyUnifiedDiffHandler : IToolHandler
{
    public ToolId Id => new("linux.text.apply_unified_diff.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var baseDirRel = Convert.ToString(invocation.Bindings["base_dir_rel"]) ?? ".";
            var diffText = Convert.ToString(invocation.Bindings["diff_text"]) ?? string.Empty;
            var strip = invocation.Bindings.TryGetValue("strip", out var s) ? Math.Max(0, Convert.ToInt32(s)) : 0;
            var maxFiles = invocation.Bindings.TryGetValue("max_files", out var mf) ? Math.Max(1, Convert.ToInt32(mf)) : 200;
            var entries = new List<(string Path, string Text)>();
            var lines = diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var i = 0;
            while (i < lines.Length)
            {
                if (!lines[i].StartsWith("--- ", StringComparison.Ordinal)) { i++; continue; }
                if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                    return ToolResultFactory.Error(Id, "text.patch_invalid", "Invalid unified diff header.");

                var newPathRaw = lines[i + 1][4..].Trim();
                var pathRel = LinuxTextPatchUtil.StripPrefix(newPathRaw, strip);
                if (string.IsNullOrWhiteSpace(pathRel))
                    return ToolResultFactory.Error(Id, "text.patch_invalid", "Invalid patch path.");

                var fullPath = ToolPath.ResolveWithinRoot(ctx, Path.Combine(baseDirRel, pathRel));
                var finalRel = ToolPath.ToRepoRelative(ctx, fullPath);
                var old = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : string.Empty;
                var originalLines = old.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
                var output = new List<string>();
                var sourceIndex = 0;
                i += 2;

                while (i < lines.Length && !lines[i].StartsWith("--- ", StringComparison.Ordinal))
                {
                    var line = lines[i];
                    if (!line.StartsWith("@@ ", StringComparison.Ordinal)) { i++; continue; }
                    var m = Regex.Match(line, "@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@");
                    if (!m.Success)
                        return ToolResultFactory.Error(Id, "text.patch_invalid", "Invalid hunk header.");

                    var startOld = int.Parse(m.Groups[1].Value);
                    while (sourceIndex < startOld - 1 && sourceIndex < originalLines.Count)
                        output.Add(originalLines[sourceIndex++]);

                    i++;
                    while (i < lines.Length && !lines[i].StartsWith("@@ ", StringComparison.Ordinal) && !lines[i].StartsWith("--- ", StringComparison.Ordinal))
                    {
                        var h = lines[i];
                        if (h.Length == 0) { i++; continue; }
                        if (h[0] == ' ')
                        {
                            var expected = h[1..];
                            if (sourceIndex >= originalLines.Count || !string.Equals(originalLines[sourceIndex], expected, StringComparison.Ordinal))
                                return ToolResultFactory.Error(Id, "text.patch_apply_failed", "Patch context mismatch.");
                            output.Add(originalLines[sourceIndex++]);
                        }
                        else if (h[0] == '-')
                        {
                            var expected = h[1..];
                            if (sourceIndex >= originalLines.Count || !string.Equals(originalLines[sourceIndex], expected, StringComparison.Ordinal))
                                return ToolResultFactory.Error(Id, "text.patch_apply_failed", "Patch removal mismatch.");
                            sourceIndex++;
                        }
                        else if (h[0] == '+')
                        {
                            output.Add(h[1..]);
                        }

                        i++;
                    }
                }

                while (sourceIndex < originalLines.Count)
                    output.Add(originalLines[sourceIndex++]);
                entries.Add((finalRel, string.Join("\n", output)));
            }

            if (entries.Count == 0)
                return ToolResultFactory.Error(Id, "text.patch_invalid", "No patch entries found.");
            if (entries.Count > maxFiles)
                return ToolResultFactory.Error(Id, "text.patch_apply_failed", "Patch exceeds max_files.");

            foreach (var e in entries.OrderBy(static x => x.Path, StringComparer.Ordinal))
            {
                var full = ToolPath.ResolveWithinRoot(ctx, e.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ctx.RepoRoot);
                File.WriteAllText(full, e.Text, Encoding.UTF8);
            }

            var ordered = entries.Select(static e => e.Path).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["applied"] = true,
                ["files_changed"] = ordered.Length,
                ["files"] = string.Join("\n", ordered),
                ["rejected"] = false
            }, true);
        }
        catch (InvalidOperationException)
        {
            return ToolResultFactory.Error(Id, "text.patch_path_escape", "Patch path escapes repo root.");
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "text.patch_apply_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextExtractBetweenMarkersHandler : IToolHandler
{
    public ToolId Id => new("linux.text.extract_between_markers.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var startMarker = Convert.ToString(invocation.Bindings["start_marker"]) ?? string.Empty;
            var endMarker = Convert.ToString(invocation.Bindings["end_marker"]) ?? string.Empty;
            var includeMarkers = invocation.Bindings.TryGetValue("include_markers", out var im) && Convert.ToBoolean(im);
            var text = File.ReadAllText(path, Encoding.UTF8);
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
                return ToolResultFactory.Error(Id, "text.marker_not_found", "Start marker not found.");
            var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            if (end < 0)
                return ToolResultFactory.Error(Id, "text.marker_not_found", "End marker not found.");
            var segment = includeMarkers ? text[start..(end + endMarker.Length)] : text[(start + startMarker.Length)..end];
            return new ToolResult(Id, new Dictionary<string, object?> { ["text"] = segment, ["found"] = true }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.read_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextInsertAfterMarkerHandler : IToolHandler
{
    public ToolId Id => new("linux.text.insert_after_marker.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var marker = Convert.ToString(invocation.Bindings["marker"]) ?? string.Empty;
            var insertText = Convert.ToString(invocation.Bindings["insert_text"]) ?? string.Empty;
            var once = invocation.Bindings.TryGetValue("once", out var o) && Convert.ToBoolean(o);
            var text = File.ReadAllText(path, Encoding.UTF8);
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                return ToolResultFactory.Error(Id, "text.marker_not_found", "Marker not found.");
            if (once && text.Contains(marker + insertText, StringComparison.Ordinal))
                return new ToolResult(Id, new Dictionary<string, object?> { ["written"] = false, ["count"] = 0 }, true);
            var updated = text.Insert(idx + marker.Length, insertText);
            File.WriteAllText(path, updated, Encoding.UTF8);
            return new ToolResult(Id, new Dictionary<string, object?> { ["written"] = true, ["count"] = 1 }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextLineEndingNormalizeHandler : IToolHandler
{
    public ToolId Id => new("linux.text.line_ending_normalize.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var mode = (Convert.ToString(invocation.Bindings.TryGetValue("mode", out var m) ? m : "lf") ?? "lf").ToLowerInvariant();
            var source = File.ReadAllText(path, Encoding.UTF8);
            var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            var converted = mode == "crlf" ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized;
            var changed = !string.Equals(source, converted, StringComparison.Ordinal);
            if (changed)
                File.WriteAllText(path, converted, Encoding.UTF8);
            return new ToolResult(Id, new Dictionary<string, object?> { ["normalized"] = true, ["changed"] = changed }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message);
        }
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
    private const int DefaultTimeoutMs = 5000;

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
            var timeoutMs = Math.Min(invocation.Bindings.TryGetValue("timeout_ms", out var timeoutObj) ? Convert.ToInt32(timeoutObj) : DefaultTimeoutMs, ctx.MaxTimeoutMs);
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

            if (invocation.Bindings.TryGetValue("env", out var envObj) && envObj is IReadOnlyDictionary<string, object?> envBindings)
            {
                foreach (var item in envBindings.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                    startInfo.Environment[item.Key] = Convert.ToString(item.Value) ?? string.Empty;
            }

            foreach (var item in ctx.EnvOverlay.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (item.Value is null)
                    startInfo.Environment.Remove(item.Key);
                else
                    startInfo.Environment[item.Key] = item.Value;
            }

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
            return ToolResultFactory.Error(Id, "tool.network_disabled", "Network access is disabled for tool execution context.");

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


internal static class GitRunner
{
    public static ToolResult RunGit(ToolId targetToolId, ToolInvocation invocation, ToolExecutionContext ctx, params string[] args)
    {
        var cwdBinding = invocation.Bindings.TryGetValue("cwd", out var cwdObj) ? Convert.ToString(cwdObj) : null;
        var exec = new LinuxProcExecHandler();
        var env = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ASKPASS"] = "/bin/true",
            ["GIT_PAGER"] = "cat",
            ["LC_ALL"] = "C",
            ["GIT_AUTHOR_NAME"] = "Shoots Tools",
            ["GIT_AUTHOR_EMAIL"] = "tools@example.local",
            ["GIT_COMMITTER_NAME"] = "Shoots Tools",
            ["GIT_COMMITTER_EMAIL"] = "tools@example.local"
        };

        var result = exec.Execute(new ToolInvocation(exec.Id, new Dictionary<string, object?>
        {
            ["file"] = "git",
            ["args"] = args.Cast<object?>().ToArray(),
            ["cwd"] = cwdBinding,
            ["timeout_ms"] = 10000,
            ["env"] = env
        }, invocation.WorkOrderId), ctx);

        if (!result.Success)
            return ToolResultFactory.Error(targetToolId, "git.exec_failed", Convert.ToString(result.Outputs["error.message"]) ?? "git failed");

        if (Convert.ToBoolean(result.Outputs["timed_out"]))
            return ToolResultFactory.Error(targetToolId, "git.timeout", "git command timed out");

        if (Convert.ToInt32(result.Outputs["exit_code"]) != 0)
        {
            var stderr = Convert.ToString(result.Outputs["stderr"]) ?? string.Empty;
            return ToolResultFactory.Error(targetToolId, "git.exit_nonzero", stderr);
        }

        return result;
    }
}

public sealed class LinuxGitRevParseHandler : IToolHandler
{
    public ToolId Id => new("linux.git.rev_parse.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = invocation.Bindings.TryGetValue("args", out var arr) && arr is IEnumerable<object?> list
            ? list.Select(static x => Convert.ToString(x) ?? string.Empty).ToArray()
            : Array.Empty<string>();
        var run = GitRunner.RunGit(Id, invocation, ctx, new[] { "rev-parse" }.Concat(args).ToArray());
        if (!run.Success)
            return run;
        return new ToolResult(Id, new Dictionary<string, object?> { ["stdout"] = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Trim() }, true);
    }
}

public sealed class LinuxGitLogHandler : IToolHandler
{
    public ToolId Id => new("linux.git.log.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var max = invocation.Bindings.TryGetValue("max", out var m) ? Math.Max(1, Convert.ToInt32(m)) : 10;
        var args = new List<string> { "--no-pager", "log", $"--max-count={max}", "--pretty=format:%H" };
        if (invocation.Bindings.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(Convert.ToString(path)))
        {
            args.Add("--");
            args.Add(Convert.ToString(path)!);
        }
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        if (!run.Success)
            return run;
        return new ToolResult(Id, new Dictionary<string, object?> { ["hashes"] = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Trim() }, true);
    }
}

public sealed class LinuxGitDiffNamesHandler : IToolHandler
{
    public ToolId Id => new("linux.git.diff_names.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string> { "diff", "--name-only" };
        if (invocation.Bindings.TryGetValue("base", out var b) && !string.IsNullOrWhiteSpace(Convert.ToString(b)))
            args.Add(Convert.ToString(b)!);
        if (invocation.Bindings.TryGetValue("head", out var h) && !string.IsNullOrWhiteSpace(Convert.ToString(h)))
            args.Add(Convert.ToString(h)!);
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        if (!run.Success)
            return run;
        var sorted = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        return new ToolResult(Id, new Dictionary<string, object?> { ["paths"] = string.Join("\n", sorted) }, true);
    }
}

public sealed class LinuxGitCheckoutHandler : IToolHandler
{
    public ToolId Id => new("linux.git.checkout.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var @ref = Convert.ToString(invocation.Bindings["ref"]) ?? string.Empty;
        var run = GitRunner.RunGit(Id, invocation, ctx, "checkout", @ref);
        return run.Success
            ? new ToolResult(Id, new Dictionary<string, object?> { ["checked_out"] = true }, true)
            : run;
    }
}

public sealed class LinuxGitAddHandler : IToolHandler
{
    public ToolId Id => new("linux.git.add.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var paths = invocation.Bindings.TryGetValue("paths", out var p) && p is IEnumerable<object?> list
            ? list.Select(static x => Convert.ToString(x) ?? string.Empty).Where(static x => x.Length > 0).ToArray()
            : Array.Empty<string>();
        var run = GitRunner.RunGit(Id, invocation, ctx, new[] { "add" }.Concat(paths).ToArray());
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["added"] = true }, true) : run;
    }
}

public sealed class LinuxGitCommitHandler : IToolHandler
{
    public ToolId Id => new("linux.git.commit.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var message = Convert.ToString(invocation.Bindings["message"]) ?? string.Empty;
        var run = GitRunner.RunGit(Id, invocation, ctx, "commit", "--no-verify", "--no-gpg-sign", "--no-edit", "-m", message);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["committed"] = true }, true) : run;
    }
}

public sealed class LinuxBuildDotnetBuildHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_build.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var project = Convert.ToString(invocation.Bindings["projectOrSln"]) ?? string.Empty;
        var cfg = invocation.Bindings.TryGetValue("configuration", out var c) ? Convert.ToString(c) : "Release";
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "build", project, "--nologo", "--verbosity", "minimal", "-c", cfg ?? "Release" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_build_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet build failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["built"] = true }, true);
    }
}

public sealed class LinuxBuildDotnetTestHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_test.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var project = Convert.ToString(invocation.Bindings["projectOrSln"]) ?? string.Empty;
        var cfg = invocation.Bindings.TryGetValue("configuration", out var c) ? Convert.ToString(c) : "Release";
        var args = new List<object?> { "test", project, "--nologo", "--verbosity", "minimal", "-c", cfg ?? "Release" };
        if (invocation.Bindings.TryGetValue("filter", out var filter) && !string.IsNullOrWhiteSpace(Convert.ToString(filter)))
        {
            args.Add("--filter");
            args.Add(Convert.ToString(filter));
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = args.ToArray(),
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_test_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet test failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["tested"] = true }, true);
    }
}

public sealed class LinuxBuildCMakeConfigureHandler : IToolHandler
{
    public ToolId Id => new("linux.build.cmake_configure.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var srcDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["srcDir"]) ?? string.Empty);
        var buildDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["buildDir"]) ?? string.Empty);
        Directory.CreateDirectory(buildDir);
        var args = new List<object?> { "-S", srcDir, "-B", buildDir };
        if (invocation.Bindings.TryGetValue("generator", out var g) && !string.IsNullOrWhiteSpace(Convert.ToString(g)))
        {
            args.Add("-G");
            args.Add(Convert.ToString(g));
        }
        if (invocation.Bindings.TryGetValue("defs", out var defsObj) && defsObj is IReadOnlyDictionary<string, object?> defs)
        {
            foreach (var kvp in defs.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                args.Add($"-D{kvp.Key}={Convert.ToString(kvp.Value) ?? string.Empty}");
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "cmake",
            ["args"] = args.ToArray(),
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.cmake_configure_failed", Convert.ToString(result.Outputs["stderr"]) ?? "cmake configure failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["configured"] = true }, true);
    }
}

public sealed class LinuxBuildCMakeBuildHandler : IToolHandler
{
    public ToolId Id => new("linux.build.cmake_build.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var buildDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["buildDir"]) ?? string.Empty);
        var args = new List<object?> { "--build", buildDir };
        if (invocation.Bindings.TryGetValue("config", out var cfg) && !string.IsNullOrWhiteSpace(Convert.ToString(cfg)))
        {
            args.Add("--config");
            args.Add(Convert.ToString(cfg));
        }
        if (invocation.Bindings.TryGetValue("target", out var t) && !string.IsNullOrWhiteSpace(Convert.ToString(t)))
        {
            args.Add("--target");
            args.Add(Convert.ToString(t));
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "cmake",
            ["args"] = args.ToArray(),
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.cmake_build_failed", Convert.ToString(result.Outputs["stderr"]) ?? "cmake build failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["built"] = true }, true);
    }
}

public sealed class LinuxProcWhichHandler : IToolHandler
{
    public ToolId Id => new("linux.proc.which.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? string.Empty;
        var pathValue = ctx.EnvOverlay.TryGetValue("PATH", out var overlayPath) ? overlayPath ?? string.Empty : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        foreach (var segment in pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment, name);
            if (File.Exists(candidate))
                return new ToolResult(Id, new Dictionary<string, object?> { ["path"] = candidate }, true);
        }

        return ToolResultFactory.Error(Id, "proc.which_not_found", $"Executable '{name}' not found.");
    }
}

public sealed class LinuxEnvGetHandler : IToolHandler
{
    public ToolId Id => new("linux.env.get.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? string.Empty;
        var value = ctx.EnvOverlay.TryGetValue(name, out var overlay) ? overlay : Environment.GetEnvironmentVariable(name);
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["value"] = value ?? string.Empty,
            ["exists"] = value is not null
        }, true);
    }
}

public sealed class LinuxEnvSetLocalHandler : IToolHandler
{
    public ToolId Id => new("linux.env.set_local.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? string.Empty;
        var value = Convert.ToString(invocation.Bindings["value"]) ?? string.Empty;
        ctx.EnvOverlay[name] = value;
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["set"] = true
        }, true);
    }
}


public sealed class LinuxGitInitHandler : IToolHandler
{
    public ToolId Id => new("linux.git.init.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "init");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["initialized"] = true }, true) : run;
    }
}

public sealed class LinuxGitBranchListHandler : IToolHandler
{
    public ToolId Id => new("linux.git.branch_list.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "branch", "--format=%(refname:short)");
        if (!run.Success)
            return run;

        var branches = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static s => s.Trim())
            .Where(static s => s.Length > 0)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToArray();

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["branches"] = string.Join("\n", branches),
            ["count"] = branches.Length
        }, true);
    }
}

public sealed class LinuxGitMergeHandler : IToolHandler
{
    public ToolId Id => new("linux.git.merge.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var @ref = Convert.ToString(invocation.Bindings["ref"]) ?? string.Empty;
        var run = GitRunner.RunGit(Id, invocation, ctx, "merge", "--no-edit", @ref);
        return run.Success
            ? new ToolResult(Id, new Dictionary<string, object?> { ["merged"] = true }, true)
            : ToolResultFactory.Error(Id, "git.merge_failed", Convert.ToString(run.Outputs.TryGetValue("error.message", out var m) ? m : "git merge failed") ?? "git merge failed");
    }
}

public sealed class LinuxGitResetHardHandler : IToolHandler
{
    public ToolId Id => new("linux.git.reset_hard.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var target = invocation.Bindings.TryGetValue("target", out var t) ? Convert.ToString(t) : "HEAD";
        var run = GitRunner.RunGit(Id, invocation, ctx, "reset", "--hard", target ?? "HEAD");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["reset"] = true }, true) : run;
    }
}

public sealed class LinuxGitCleanFdHandler : IToolHandler
{
    public ToolId Id => new("linux.git.clean_fd.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "clean", "-fd");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["cleaned"] = true }, true) : run;
    }
}

public sealed class LinuxFsEnsureDirHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.ensure_dir.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);
            return new ToolResult(Id, new Dictionary<string, object?> { ["ensured"] = true, ["created"] = !existed }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.ensure_dir_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsExistsHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.exists.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var file = File.Exists(path);
            var dir = Directory.Exists(path);
            return new ToolResult(Id, new Dictionary<string, object?> { ["exists"] = file || dir, ["is_file"] = file, ["is_dir"] = dir }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.exists_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsFindFilesHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.find_files.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var pattern = invocation.Bindings.TryGetValue("pattern", out var p) ? Convert.ToString(p) : "*";
            var max = invocation.Bindings.TryGetValue("max_entries", out var m) ? Math.Max(1, Convert.ToInt32(m)) : 200;
            var files = Directory.EnumerateFiles(path, pattern ?? "*", SearchOption.AllDirectories)
                .Select(f => ToolPath.ToRepoRelative(ctx, f))
                .OrderBy(static f => f, StringComparer.Ordinal)
                .Take(max)
                .ToArray();
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["files"] = string.Join("\n", files),
                ["count"] = files.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.find_files_failed", ex.Message);
        }
    }
}

public sealed class LinuxFsGlobHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.glob.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? string.Empty);
            var pattern = Convert.ToString(invocation.Bindings["pattern"]) ?? "*";
            var max = invocation.Bindings.TryGetValue("max_entries", out var m) ? Math.Max(1, Convert.ToInt32(m)) : 200;
            var matches = Directory.EnumerateFileSystemEntries(path, pattern, SearchOption.TopDirectoryOnly)
                .Select(f => ToolPath.ToRepoRelative(ctx, f))
                .OrderBy(static f => f, StringComparer.Ordinal)
                .Take(max)
                .ToArray();
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["matches"] = string.Join("\n", matches),
                ["count"] = matches.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.glob_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextRgHandler : IToolHandler
{
    public ToolId Id => new("linux.text.rg.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var pattern = Convert.ToString(invocation.Bindings["pattern"]) ?? string.Empty;
        var path = invocation.Bindings.TryGetValue("path", out var p) ? Convert.ToString(p) : ".";
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "rg",
            ["args"] = new object?[] { "--no-heading", "--line-number", pattern, path ?? "." },
            ["cwd"] = ".",
            ["max_output_bytes"] = ctx.MaxBytesOut,
            ["timeout_ms"] = Math.Min(30000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);

        if (!result.Success)
            return ToolResultFactory.Error(Id, "text.rg_failed", Convert.ToString(result.Outputs["error.message"]) ?? "rg failed");

        var outText = Convert.ToString(result.Outputs["stdout"]) ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(outText);
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["matches"] = outText,
            ["truncated"] = bytes >= ctx.MaxBytesOut,
            ["bytes"] = Math.Min(bytes, ctx.MaxBytesOut)
        }, true);
    }
}

public sealed class LinuxTextReplaceInFilesHandler : IToolHandler
{
    public ToolId Id => new("linux.text.replace_in_files.v1");
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".md", ".txt", ".xml", ".yml", ".yaml", ".props", ".targets", ".sln" };

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var root = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? ".");
            var search = Convert.ToString(invocation.Bindings["search"]) ?? string.Empty;
            var replace = Convert.ToString(invocation.Bindings["replace"]) ?? string.Empty;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => Allowed.Contains(Path.GetExtension(f)))
                .OrderBy(static f => f, StringComparer.Ordinal)
                .ToArray();

            var perFile = new List<string>();
            var total = 0;
            foreach (var file in files)
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var count = CountOccurrences(text, search);
                if (count == 0)
                    continue;
                var updated = text.Replace(search, replace, StringComparison.Ordinal);
                File.WriteAllText(file, updated, Encoding.UTF8);
                perFile.Add($"{ToolPath.ToRepoRelative(ctx, file)}:{count}");
                total += count;
            }

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["total_replacements"] = total,
                ["files"] = string.Join("\n", perFile)
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "text.replace_in_files_failed", ex.Message);
        }
    }

    private static int CountOccurrences(string text, string search)
    {
        if (search.Length == 0)
            return 0;

        var count = 0;
        var idx = 0;
        while (true)
        {
            idx = text.IndexOf(search, idx, StringComparison.Ordinal);
            if (idx < 0)
                break;

            count++;
            idx += search.Length;
        }

        return count;
    }
}

public sealed class LinuxBuildDotnetRestoreHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_restore.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var target = Convert.ToString(invocation.Bindings["projectOrSln"]) ?? string.Empty;
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "restore", target, "--nologo", "--verbosity", "minimal" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_restore_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet restore failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["restored"] = true }, true);
    }
}

public sealed class LinuxBuildDotnetPackHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_pack.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var target = Convert.ToString(invocation.Bindings["projectOrSln"]) ?? string.Empty;
        var cfg = invocation.Bindings.TryGetValue("configuration", out var c) ? Convert.ToString(c) : "Release";
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "pack", target, "--nologo", "--verbosity", "minimal", "-c", cfg ?? "Release" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_pack_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet pack failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["packed"] = true }, true);
    }
}

public sealed class LinuxBuildCMakeCTestHandler : IToolHandler
{
    public ToolId Id => new("linux.build.cmake_ctest.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var buildDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["buildDir"]) ?? string.Empty);
        var args = new List<object?> { "--test-dir", buildDir, "--output-on-failure" };
        if (invocation.Bindings.TryGetValue("config", out var cfg) && !string.IsNullOrWhiteSpace(Convert.ToString(cfg)))
        {
            args.Add("-C"); args.Add(Convert.ToString(cfg));
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "ctest",
            ["args"] = args.ToArray(),
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.cmake_ctest_failed", Convert.ToString(result.Outputs["stderr"]) ?? "ctest failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["tested"] = true }, true);
    }
}

public sealed class LinuxArchiveTarGzHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.tar_gz.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var source = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["source_dir"]) ?? string.Empty);
        var tarPath = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["tar_path"]) ?? string.Empty);
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath) ?? ctx.RepoRoot);
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "tar",
            ["args"] = new object?[] { "-czf", tarPath, "-C", source, "." },
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "archive.tar_gz_failed", Convert.ToString(result.Outputs["stderr"]) ?? "tar create failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["archived"] = true }, true);
    }
}

public sealed class LinuxArchiveUntarGzHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.untar_gz.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var tarPath = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["tar_path"]) ?? string.Empty);
        var dest = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["dest_dir"]) ?? string.Empty);
        Directory.CreateDirectory(dest);
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "tar",
            ["args"] = new object?[] { "-xzf", tarPath, "-C", dest },
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "archive.untar_gz_failed", Convert.ToString(result.Outputs["stderr"]) ?? "tar extract failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["extracted"] = true }, true);
    }
}

public sealed class LinuxArtifactsHashTreeHandler : IToolHandler
{
    public ToolId Id => new("linux.artifacts.hash_tree.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var root = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path"]) ?? ".");
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(static f => f, StringComparer.Ordinal)
                .ToArray();
            var lines = new List<string>(files.Length);
            foreach (var f in files)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(f);
                var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                lines.Add($"{hash}  {Path.GetRelativePath(root, f).Replace('\\', '/')}");
            }
            var joined = string.Join("\n", lines);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["hashes"] = ToolResultFactory.TruncateUtf8(joined, ctx.MaxBytesOut),
                ["count"] = lines.Count,
                ["truncated"] = Encoding.UTF8.GetByteCount(joined) > ctx.MaxBytesOut
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "artifacts.hash_tree_failed", ex.Message);
        }
    }
}


public sealed class LinuxGitFetchHandler : IToolHandler
{
    public ToolId Id => new("linux.git.fetch.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "fetch", "--prune");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["fetched"] = true }, true) : run;
    }
}

public sealed class LinuxGitPullFfOnlyHandler : IToolHandler
{
    public ToolId Id => new("linux.git.pull_ff_only.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "pull", "--ff-only");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["pulled"] = true }, true) : run;
    }
}

public sealed class LinuxGitMergeAbortHandler : IToolHandler
{
    public ToolId Id => new("linux.git.merge_abort.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "merge", "--abort");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["aborted"] = true }, true) : run;
    }
}

public sealed class LinuxGitRebaseAbortHandler : IToolHandler
{
    public ToolId Id => new("linux.git.rebase_abort.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "rebase", "--abort");
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["aborted"] = true }, true) : run;
    }
}

public sealed class LinuxGitStatusPorcelainHandler : IToolHandler
{
    public ToolId Id => new("linux.git.status_porcelain.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "status", "--porcelain");
        if (!run.Success)
            return run;

        var text = Convert.ToString(run.Outputs["stdout"]) ?? string.Empty;
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["porcelain"] = ToolResultFactory.TruncateUtf8(text, ctx.MaxBytesOut),
            ["clean"] = string.IsNullOrWhiteSpace(text)
        }, true);
    }
}

public sealed class LinuxGitCurrentBranchHandler : IToolHandler
{
    public ToolId Id => new("linux.git.current_branch.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "rev-parse", "--abbrev-ref", "HEAD");
        if (!run.Success)
            return run;

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["branch"] = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Trim()
        }, true);
    }
}

public sealed class LinuxGitRemoteListHandler : IToolHandler
{
    public ToolId Id => new("linux.git.remote_list.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "remote");
        if (!run.Success)
            return run;

        var remotes = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static s => s.Trim())
            .Where(static s => s.Length > 0)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToArray();

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["remotes"] = string.Join("\n", remotes),
            ["count"] = remotes.Length
        }, true);
    }
}

public sealed class LinuxFsCwdHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.cwd.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
        => new(Id, new Dictionary<string, object?>
        {
            ["cwd"] = ToolPath.ToRepoRelative(ctx, ctx.WorkingDirectory)
        }, true);
}

public sealed class LinuxFsPathJoinHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.path_join.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var parts = invocation.Bindings.TryGetValue("parts", out var value) && value is IEnumerable<object?> list
            ? list.Select(static p => Convert.ToString(p) ?? string.Empty).ToArray()
            : Array.Empty<string>();

        var joined = parts.Length == 0 ? string.Empty : Path.Combine(parts);
        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["path"] = joined.Replace('\\', '/')
        }, true);
    }
}

public sealed class LinuxFsTempDirHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.temp_dir.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var seed = Convert.ToString(invocation.Bindings["seed"]) ?? string.Empty;
            var rootRel = invocation.Bindings.TryGetValue("root", out var r) ? Convert.ToString(r) : ".tmp";
            var root = ToolPath.ResolveWithinRoot(ctx, rootRel ?? ".tmp");
            Directory.CreateDirectory(root);

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
                .ToLowerInvariant()[..12];
            var path = Path.Combine(root, hash);
            Directory.CreateDirectory(path);

            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["path"] = ToolPath.ToRepoRelative(ctx, path),
                ["created"] = true
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "fs.temp_dir_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextTrimHandler : IToolHandler
{
    public ToolId Id => new("linux.text.trim.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var text = Convert.ToString(invocation.Bindings["text"]) ?? string.Empty;
        return new ToolResult(Id, new Dictionary<string, object?> { ["text"] = text.Trim() }, true);
    }
}

public sealed class LinuxTextToJsonHandler : IToolHandler
{
    public ToolId Id => new("linux.text.to_json.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var map = invocation.Bindings.TryGetValue("map", out var m) && m is IReadOnlyDictionary<string, object?> dict
                ? dict
                : new Dictionary<string, object?>();
            var ordered = map.OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
            var json = System.Text.Json.JsonSerializer.Serialize(ordered);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["json"] = ToolResultFactory.TruncateUtf8(json, ctx.MaxBytesOut)
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "text.to_json_failed", ex.Message);
        }
    }
}

public sealed class LinuxTextFromJsonHandler : IToolHandler
{
    public ToolId Id => new("linux.text.from_json.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var json = Convert.ToString(invocation.Bindings["json"]) ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(json) > ctx.MaxBytesOut)
                return ToolResultFactory.Error(Id, "text.json_too_large", "json exceeds max bytes");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var keys = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(static x => x, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["keys"] = string.Join("\n", keys),
                ["count"] = keys.Length
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "text.from_json_failed", ex.Message);
        }
    }
}

public sealed class LinuxBuildDotnetSlnListProjectsHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_sln_list_projects.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var sln = Convert.ToString(invocation.Bindings["sln"]) ?? string.Empty;
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "sln", sln, "list" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(60000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);

        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_sln_list_projects_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet sln list failed");

        var lines = (Convert.ToString(result.Outputs["stdout"]) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0 && !l.StartsWith("Project(s)", StringComparison.Ordinal))
            .OrderBy(static l => l, StringComparer.Ordinal)
            .ToArray();

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["projects"] = string.Join("\n", lines),
            ["count"] = lines.Length
        }, true);
    }
}

public sealed class LinuxBuildDotnetListPackagesHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_list_packages.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var target = Convert.ToString(invocation.Bindings["projectOrSln"]) ?? string.Empty;
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "list", target, "package" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(60000, ctx.MaxTimeoutMs),
            ["max_output_bytes"] = ctx.MaxBytesOut
        }, invocation.WorkOrderId), ctx);

        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_list_packages_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet list package failed");

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["packages"] = Convert.ToString(result.Outputs["stdout"]) ?? string.Empty
        }, true);
    }
}

public sealed class LinuxProcPsHandler : IToolHandler
{
    public ToolId Id => new("linux.proc.ps.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "ps",
            ["args"] = new object?[] { "-eo", "pid,comm" },
            ["timeout_ms"] = Math.Min(10000, ctx.MaxTimeoutMs),
            ["max_output_bytes"] = ctx.MaxBytesOut
        }, invocation.WorkOrderId), ctx);

        if (!result.Success)
            return ToolResultFactory.Error(Id, "proc.ps_failed", Convert.ToString(result.Outputs["error.message"]) ?? "ps failed");

        var lines = (Convert.ToString(result.Outputs["stdout"]) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        return new ToolResult(Id, new Dictionary<string, object?>
        {
            ["processes"] = string.Join("\n", lines),
            ["count"] = lines.Length
        }, true);
    }
}

public sealed class LinuxArchiveZipDirHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.zip_dir.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
        => new LinuxArchiveZipHandler().Execute(new ToolInvocation(new ToolId("linux.archive.zip.v1"), new Dictionary<string, object?>
        {
            ["source_dir"] = invocation.Bindings["source_dir"],
            ["zip_path"] = invocation.Bindings["zip_path"],
            ["include_hidden"] = invocation.Bindings.TryGetValue("include_hidden", out var h) ? h : false
        }, invocation.WorkOrderId), ctx) with { ToolId = Id };
}

public sealed class LinuxArchiveUnzipToDirHandler : IToolHandler
{
    public ToolId Id => new("linux.archive.unzip_to_dir.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
        => new LinuxArchiveUnzipHandler().Execute(new ToolInvocation(new ToolId("linux.archive.unzip.v1"), new Dictionary<string, object?>
        {
            ["zip_path"] = invocation.Bindings["zip_path"],
            ["dest_dir"] = invocation.Bindings["dest_dir"],
            ["overwrite"] = invocation.Bindings.TryGetValue("overwrite", out var o) ? o : false
        }, invocation.WorkOrderId), ctx) with { ToolId = Id };
}

public sealed class LinuxGitTagAnnotatedHandler : IToolHandler
{
    public ToolId Id => new("linux.git.tag_annotated.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var tag = Convert.ToString(invocation.Bindings["tag"]) ?? string.Empty;
        var message = Convert.ToString(invocation.Bindings["message"]) ?? string.Empty;
        var run = GitRunner.RunGit(Id, invocation, ctx, "tag", "-a", tag, "-m", message);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["tagged"] = true }, true) : run;
    }
}

public sealed class LinuxGitPushTagHandler : IToolHandler
{
    public ToolId Id => new("linux.git.push_tag.v1");

    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var tag = Convert.ToString(invocation.Bindings["tag"]) ?? string.Empty;
        var remote = invocation.Bindings.TryGetValue("remote", out var r) ? Convert.ToString(r) : "origin";
        var run = GitRunner.RunGit(Id, invocation, ctx, "push", remote ?? "origin", tag);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["pushed"] = true }, true) : run;
    }
}


public sealed class LinuxGitFetchAllHandler : IToolHandler
{
    public ToolId Id => new("linux.git.fetch_all.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string> { "fetch", "--all" };
        if (!invocation.Bindings.TryGetValue("prune", out var pruneObj) || Convert.ToBoolean(pruneObj))
            args.Add("--prune");
        if (invocation.Bindings.TryGetValue("tags", out var tagsObj) && Convert.ToBoolean(tagsObj))
            args.Add("--tags");
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["fetched"] = true }, true) : run;
    }
}

public sealed class LinuxGitPullRebaseFfOnlyHandler : IToolHandler
{
    public ToolId Id => new("linux.git.pull_rebase_ff_only.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var remote = invocation.Bindings.TryGetValue("remote", out var remoteObj) ? Convert.ToString(remoteObj) : "origin";
        var branch = invocation.Bindings.TryGetValue("branch", out var branchObj) ? Convert.ToString(branchObj) : null;
        var args = new List<string> { "pull", "--ff-only", "--rebase", remote ?? "origin" };
        if (!string.IsNullOrWhiteSpace(branch))
            args.Add(branch);
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["pulled"] = true }, true) : run;
    }
}

public sealed class LinuxGitBranchCreateHandler : IToolHandler
{
    public ToolId Id => new("linux.git.branch_create.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? string.Empty;
        var start = invocation.Bindings.TryGetValue("start_point", out var startObj) ? Convert.ToString(startObj) : null;
        var args = string.IsNullOrWhiteSpace(start) ? new[] { "branch", name } : new[] { "branch", name, start! };
        var run = GitRunner.RunGit(Id, invocation, ctx, args);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["created"] = true }, true) : run;
    }
}

public sealed class LinuxGitBranchDeleteHandler : IToolHandler
{
    public ToolId Id => new("linux.git.branch_delete.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? string.Empty;
        var force = invocation.Bindings.TryGetValue("force", out var f) && Convert.ToBoolean(f);
        var run = GitRunner.RunGit(Id, invocation, ctx, "branch", force ? "-D" : "-d", name);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["deleted"] = true }, true) : run;
    }
}

public sealed class LinuxGitTagListHandler : IToolHandler
{
    public ToolId Id => new("linux.git.tag_list.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var pattern = invocation.Bindings.TryGetValue("pattern", out var p) ? Convert.ToString(p) : null;
        var args = string.IsNullOrWhiteSpace(pattern) ? new[] { "tag", "--list" } : new[] { "tag", "--list", pattern! };
        var run = GitRunner.RunGit(Id, invocation, ctx, args);
        if (!run.Success)
            return run;
        var tags = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim()).Where(static x => x.Length > 0).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new ToolResult(Id, new Dictionary<string, object?> { ["tags"] = string.Join("\n", tags), ["count"] = tags.Length }, true);
    }
}

public sealed class LinuxGitDescribeHandler : IToolHandler
{
    public ToolId Id => new("linux.git.describe.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string> { "describe", "--tags" };
        if (invocation.Bindings.TryGetValue("match", out var m) && !string.IsNullOrWhiteSpace(Convert.ToString(m)))
        {
            args.Add("--match");
            args.Add(Convert.ToString(m)!);
        }
        if (invocation.Bindings.TryGetValue("dirty_mark", out var d) && !string.IsNullOrWhiteSpace(Convert.ToString(d)))
            args.Add($"--dirty={Convert.ToString(d)}");
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        if (!run.Success)
            return run;
        return new ToolResult(Id, new Dictionary<string, object?> { ["describe"] = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Trim() }, true);
    }
}

public sealed class LinuxGitChangedFilesHandler : IToolHandler
{
    public ToolId Id => new("linux.git.changed_files.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string>();
        if (invocation.Bindings.TryGetValue("staged", out var st) && Convert.ToBoolean(st))
            args.AddRange(new[] { "diff", "--name-only", "--cached" });
        else
            args.AddRange(new[] { "diff", "--name-only" });
        if (invocation.Bindings.TryGetValue("base", out var b) && !string.IsNullOrWhiteSpace(Convert.ToString(b)))
            args.Add(Convert.ToString(b)!);
        if (invocation.Bindings.TryGetValue("head", out var h) && !string.IsNullOrWhiteSpace(Convert.ToString(h)))
            args.Add(Convert.ToString(h)!);
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        if (!run.Success)
            return run;
        var files = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim()).Where(static x => x.Length > 0).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new ToolResult(Id, new Dictionary<string, object?> { ["files"] = string.Join("\n", files), ["count"] = files.Length }, true);
    }
}

public sealed class LinuxGitIsCleanHandler : IToolHandler
{
    public ToolId Id => new("linux.git.is_clean.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var run = GitRunner.RunGit(Id, invocation, ctx, "status", "--porcelain");
        if (!run.Success)
            return run;
        var output = Convert.ToString(run.Outputs["stdout"]) ?? string.Empty;
        return new ToolResult(Id, new Dictionary<string, object?> { ["is_clean"] = string.IsNullOrWhiteSpace(output) }, true);
    }
}

public sealed class LinuxBuildDotnetCleanHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_clean.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var target = invocation.Bindings.TryGetValue("sln_or_proj_rel", out var t) ? Convert.ToString(t) : string.Empty;
        var cfg = invocation.Bindings.TryGetValue("configuration", out var c) ? Convert.ToString(c) : "Release";
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = new object?[] { "clean", target ?? string.Empty, "--nologo", "--verbosity", "minimal", "-c", cfg ?? "Release" },
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_clean_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet clean failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["cleaned"] = true }, true);
    }
}

public sealed class LinuxBuildDotnetPublishHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_publish.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var proj = Convert.ToString(invocation.Bindings["proj_rel"]) ?? string.Empty;
        var cfg = invocation.Bindings.TryGetValue("configuration", out var c) ? Convert.ToString(c) : "Release";
        var args = new List<object?> { "publish", proj, "--nologo", "--verbosity", "minimal", "-c", cfg ?? "Release" };
        if (invocation.Bindings.TryGetValue("runtime", out var runtime) && !string.IsNullOrWhiteSpace(Convert.ToString(runtime)))
        {
            args.Add("-r"); args.Add(Convert.ToString(runtime));
        }
        if (invocation.Bindings.TryGetValue("self_contained", out var sc))
        {
            args.Add("--self-contained"); args.Add(Convert.ToBoolean(sc) ? "true" : "false");
        }
        if (invocation.Bindings.TryGetValue("output_rel", out var outRel) && !string.IsNullOrWhiteSpace(Convert.ToString(outRel)))
        {
            args.Add("-o"); args.Add(ToolPath.ResolveWithinRoot(ctx, Convert.ToString(outRel) ?? string.Empty));
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = args.ToArray(),
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_publish_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet publish failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["published"] = true }, true);
    }
}

public sealed class LinuxBuildDotnetFormatVerifyHandler : IToolHandler
{
    public ToolId Id => new("linux.build.dotnet_format_verify.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var sln = invocation.Bindings.TryGetValue("sln_rel", out var s) ? Convert.ToString(s) : string.Empty;
        var severity = invocation.Bindings.TryGetValue("severity", out var sev) ? Convert.ToString(sev) : null;
        var args = new List<object?> { "format" };
        if (!string.IsNullOrWhiteSpace(sln)) args.Add(sln);
        args.Add("--verify-no-changes");
        if (!string.IsNullOrWhiteSpace(severity)) { args.Add("--severity"); args.Add(severity); }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "dotnet",
            ["args"] = args.ToArray(),
            ["cwd"] = invocation.Bindings.TryGetValue("cwd", out var cwd) ? cwd : null,
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success)
            return result;
        if (Convert.ToInt32(result.Outputs["exit_code"]) == 127 || Convert.ToInt32(result.Outputs["exit_code"]) == 1 && (Convert.ToString(result.Outputs["stderr"]) ?? string.Empty).Contains("could not execute", StringComparison.OrdinalIgnoreCase))
            return ToolResultFactory.Error(Id, "tool.unavailable", "dotnet format is unavailable");
        if (Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.dotnet_format_verify_failed", Convert.ToString(result.Outputs["stderr"]) ?? "dotnet format verify failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["verified"] = true }, true);
    }
}

public sealed class LinuxBuildCMakeInstallHandler : IToolHandler
{
    public ToolId Id => new("linux.build.cmake_install.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var buildDir = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["build_dir_rel"]) ?? string.Empty);
        var args = new List<object?> { "--install", buildDir };
        if (invocation.Bindings.TryGetValue("prefix_rel", out var p) && !string.IsNullOrWhiteSpace(Convert.ToString(p)))
        {
            args.Add("--prefix"); args.Add(ToolPath.ResolveWithinRoot(ctx, Convert.ToString(p) ?? string.Empty));
        }
        var proc = new LinuxProcExecHandler();
        var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?>
        {
            ["file"] = "cmake",
            ["args"] = args.ToArray(),
            ["timeout_ms"] = Math.Min(600000, ctx.MaxTimeoutMs)
        }, invocation.WorkOrderId), ctx);
        if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "build.cmake_install_failed", Convert.ToString(result.Outputs["stderr"]) ?? "cmake install failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["installed"] = true }, true);
    }
}

public sealed class LinuxSysDiskUsageHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.disk_usage.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, invocation.Bindings.TryGetValue("path_rel", out var p) ? Convert.ToString(p) ?? "." : ".");
            var maxDepth = invocation.Bindings.TryGetValue("max_depth", out var d) ? Math.Max(0, Convert.ToInt32(d)) : 3;
            var lines = new List<string>();
            Walk(path, 0, maxDepth, lines, ctx);
            var ordered = lines.OrderBy(static l => l, StringComparer.Ordinal).ToArray();
            var text = string.Join("\n", ordered);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["entries"] = ToolResultFactory.TruncateUtf8(text, ctx.MaxBytesOut),
                ["count"] = ordered.Length,
                ["truncated"] = Encoding.UTF8.GetByteCount(text) > ctx.MaxBytesOut
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "sys.disk_usage_failed", ex.Message);
        }
    }

    private static void Walk(string path, int depth, int maxDepth, List<string> lines, ToolExecutionContext ctx)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.Directory))
        {
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                size += new FileInfo(f).Length;
            lines.Add($"{ToolPath.ToRepoRelative(ctx, path)}	{size}");
            if (depth >= maxDepth) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).OrderBy(static x => x, StringComparer.Ordinal))
                Walk(child, depth + 1, maxDepth, lines, ctx);
        }
        else
        {
            lines.Add($"{ToolPath.ToRepoRelative(ctx, path)}	{new FileInfo(path).Length}");
        }
    }
}

public sealed class LinuxSysMemInfoHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.meminfo.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var lines = File.ReadAllLines("/proc/meminfo")
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0)
                .Select(static line => line.Replace(":", string.Empty))
                .OrderBy(static line => line, StringComparer.Ordinal)
                .ToArray();
            var joined = string.Join("\n", lines);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["meminfo"] = ToolResultFactory.TruncateUtf8(joined, ctx.MaxBytesOut),
                ["count"] = lines.Length,
                ["truncated"] = Encoding.UTF8.GetByteCount(joined) > ctx.MaxBytesOut
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "sys.meminfo_failed", ex.Message);
        }
    }
}


public sealed class LinuxFsTouchHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.touch.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.RepoRoot);
            if (!File.Exists(path)) File.WriteAllText(path, string.Empty, Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new ToolResult(Id, new Dictionary<string, object?> { ["touched"] = true }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message); }
    }
}

public sealed class LinuxFsEnsureFileHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.ensure_file.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var hasContent = invocation.Bindings.TryGetValue("content", out var c);
            var content = Convert.ToString(c) ?? string.Empty;
            var existed = File.Exists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.RepoRoot);
            if (!existed) File.WriteAllText(path, hasContent ? content : string.Empty, Encoding.UTF8);
            else if (hasContent) File.WriteAllText(path, content, Encoding.UTF8);
            return new ToolResult(Id, new Dictionary<string, object?> { ["created"] = !existed, ["exists"] = true }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message); }
    }
}

public sealed class LinuxFsChmodHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.chmod.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var mode = Convert.ToString(invocation.Bindings["mode_octal"]) ?? string.Empty;
        if (!Regex.IsMatch(mode, "^[0-7]{3,4}$", RegexOptions.CultureInvariant))
            return ToolResultFactory.Error(Id, "fs.chmod_failed", "Invalid octal mode.");
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var recursive = invocation.Bindings.TryGetValue("recursive", out var r) && Convert.ToBoolean(r);
            var args = recursive ? new object?[] { mode, "-R", path } : new object?[] { mode, path };
            var proc = new LinuxProcExecHandler();
            var result = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?> { ["file"] = "chmod", ["args"] = args }, invocation.WorkOrderId), ctx);
            if (!result.Success || Convert.ToInt32(result.Outputs["exit_code"]) != 0)
                return ToolResultFactory.Error(Id, "fs.chmod_failed", Convert.ToString(result.Outputs["stderr"]) ?? "chmod failed");
            return new ToolResult(Id, new Dictionary<string, object?> { ["changed"] = true }, true);
        }
        catch (UnauthorizedAccessException ex) { return ToolResultFactory.Error(Id, "fs.permission_denied", ex.Message); }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.chmod_failed", ex.Message); }
    }
}

public sealed class LinuxFsSymlinkHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.symlink.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var link = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["link_path_rel"]) ?? string.Empty);
            var targetRel = Convert.ToString(invocation.Bindings["target_rel"]) ?? string.Empty;
            var target = ToolPath.ResolveWithinRoot(ctx, targetRel);
            Directory.CreateDirectory(Path.GetDirectoryName(link) ?? ctx.RepoRoot);
            if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            File.CreateSymbolicLink(link, target);
            return new ToolResult(Id, new Dictionary<string, object?> { ["linked"] = true }, true);
        }
        catch (InvalidOperationException) { return ToolResultFactory.Error(Id, "fs.write_failed", "path escapes repo root"); }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.write_failed", ex.Message); }
    }
}

public sealed class LinuxFsReadlinkHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.readlink.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var fi = new FileInfo(path);
            var target = fi.ResolveLinkTarget(false);
            return new ToolResult(Id, new Dictionary<string, object?> { ["target"] = target?.FullName ?? string.Empty }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.read_failed", ex.Message); }
    }
}

public sealed class LinuxFsRealpathHandler : IToolHandler
{
    public ToolId Id => new("linux.fs.realpath.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            var real = Path.GetFullPath(path);
            var rel = ToolPath.ToRepoRelative(ctx, ToolPath.ResolveWithinRoot(ctx, Path.GetRelativePath(ctx.RepoRoot, real)));
            return new ToolResult(Id, new Dictionary<string, object?> { ["real_rel"] = rel.Replace('\\', '/') }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "fs.read_failed", ex.Message); }
    }
}

public sealed class LinuxGitCloneDepthHandler : IToolHandler
{
    public ToolId Id => new("linux.git.clone_depth.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        if (!ctx.AllowNetwork)
            return ToolResultFactory.Error(Id, "tool.network_disabled", "Network access is disabled for tool execution context.");
        var args = new List<string> { "clone" };
        var branch = invocation.Bindings.TryGetValue("branch", out var b) ? Convert.ToString(b) : null;
        var depth = invocation.Bindings.TryGetValue("depth", out var d) ? Convert.ToInt32(d) : 1;
        if (!string.IsNullOrWhiteSpace(branch)) { args.Add("--branch"); args.Add(branch!); }
        if (depth > 0) { args.Add("--depth"); args.Add(depth.ToString()); }
        args.Add(Convert.ToString(invocation.Bindings["url"]) ?? string.Empty);
        args.Add(ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["dest_rel"]) ?? string.Empty));
        var proc = new LinuxProcExecHandler();
        var run = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?> { ["file"] = "git", ["args"] = args.Cast<object?>().ToArray() }, invocation.WorkOrderId), ctx);
        if (!run.Success || Convert.ToInt32(run.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "git.clone_failed", Convert.ToString(run.Outputs["stderr"]) ?? "git clone failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["cloned"] = true }, true);
    }
}

public sealed class LinuxGitSubmoduleUpdateHandler : IToolHandler
{
    public ToolId Id => new("linux.git.submodule_update.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string> { "submodule", "update" };
        if (invocation.Bindings.TryGetValue("init", out var i) && Convert.ToBoolean(i)) args.Add("--init");
        if (invocation.Bindings.TryGetValue("recursive", out var r) && Convert.ToBoolean(r)) args.Add("--recursive");
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["updated"] = true }, true) : run;
    }
}

public sealed class LinuxGitRemoteSetUrlHandler : IToolHandler
{
    public ToolId Id => new("linux.git.remote_set_url.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var name = Convert.ToString(invocation.Bindings["name"]) ?? "origin";
        var url = Convert.ToString(invocation.Bindings["url"]) ?? string.Empty;
        var run = GitRunner.RunGit(Id, invocation, ctx, "remote", "set-url", name, url);
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["set"] = true }, true) : run;
    }
}

public sealed class LinuxGitCommitAmendHandler : IToolHandler
{
    public ToolId Id => new("linux.git.commit_amend.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var args = new List<string> { "commit", "--amend", "--no-verify", "--no-gpg-sign" };
        var noEdit = invocation.Bindings.TryGetValue("no_edit", out var ne) && Convert.ToBoolean(ne);
        var message = invocation.Bindings.TryGetValue("message", out var m) ? Convert.ToString(m) : null;
        if (noEdit || string.IsNullOrWhiteSpace(message)) args.Add("--no-edit");
        else { args.Add("-m"); args.Add(message!); }
        var run = GitRunner.RunGit(Id, invocation, ctx, args.ToArray());
        return run.Success ? new ToolResult(Id, new Dictionary<string, object?> { ["amended"] = true }, true) : run;
    }
}

public sealed class LinuxSysUnameHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.uname.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var argsText = Convert.ToString(invocation.Bindings.TryGetValue("args", out var a) ? a : "-a") ?? "-a";
        var proc = new LinuxProcExecHandler();
        var run = proc.Execute(new ToolInvocation(proc.Id, new Dictionary<string, object?> { ["file"] = "uname", ["args"] = argsText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Cast<object?>().ToArray() }, invocation.WorkOrderId), ctx);
        if (!run.Success || Convert.ToInt32(run.Outputs["exit_code"]) != 0)
            return ToolResultFactory.Error(Id, "sys.uname_failed", Convert.ToString(run.Outputs["stderr"]) ?? "uname failed");
        return new ToolResult(Id, new Dictionary<string, object?> { ["uname"] = (Convert.ToString(run.Outputs["stdout"]) ?? string.Empty).Trim() }, true);
    }
}

public sealed class LinuxSysCpuinfoHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.cpuinfo.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var lines = File.ReadAllLines("/proc/cpuinfo").OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            var joined = string.Join("\n", lines);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["cpuinfo"] = ToolResultFactory.TruncateUtf8(joined, ctx.MaxBytesOut),
                ["count"] = lines.Count(l => l.StartsWith("processor", StringComparison.Ordinal)),
                ["truncated"] = Encoding.UTF8.GetByteCount(joined) > ctx.MaxBytesOut
            }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "sys.cpuinfo_failed", ex.Message); }
    }
}

public sealed class LinuxSysDiskFreeHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.disk_free.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, invocation.Bindings.TryGetValue("path_rel", out var p) ? Convert.ToString(p) ?? "." : ".");
            var root = Path.GetPathRoot(path) ?? "/";
            var drive = new DriveInfo(root);
            return new ToolResult(Id, new Dictionary<string, object?> { ["bytes_free"] = drive.AvailableFreeSpace, ["bytes_total"] = drive.TotalSize }, true);
        }
        catch (Exception ex) { return ToolResultFactory.Error(Id, "sys.disk_free_failed", ex.Message); }
    }
}

public sealed class LinuxSysEnvDumpSafeHandler : IToolHandler
{
    public ToolId Id => new("linux.sys.env_dump_safe.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        var allow = invocation.Bindings.TryGetValue("allowlist", out var a)
            ? (Convert.ToString(a) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { "HOME", "PATH", "USER", "SHELL", "LANG", "TERM" };
        var lines = allow.OrderBy(static x => x, StringComparer.Ordinal)
            .Select(key => $"{key}={Environment.GetEnvironmentVariable(key) ?? string.Empty}")
            .ToArray();
        return new ToolResult(Id, new Dictionary<string, object?> { ["env"] = string.Join("\n", lines) }, true);
    }
}

public sealed class LinuxHashFileSha256Handler : IToolHandler
{
    public ToolId Id => new("linux.hash.file_sha256.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var path = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? string.Empty);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            return new ToolResult(Id, new Dictionary<string, object?> { ["sha256"] = hash }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "hash.file_sha256_failed", ex.Message);
        }
    }
}

public sealed class LinuxHashDirManifestHandler : IToolHandler
{
    public ToolId Id => new("linux.hash.dir_manifest.v1");
    public ToolResult Execute(ToolInvocation invocation, ToolExecutionContext ctx)
    {
        try
        {
            var root = ToolPath.ResolveWithinRoot(ctx, Convert.ToString(invocation.Bindings["path_rel"]) ?? ".");
            var lines = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(static f => f, StringComparer.Ordinal))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                lines.Add($"{hash}  {Path.GetRelativePath(root, file).Replace('\\', '/')}");
            }
            var joined = string.Join("\n", lines);
            return new ToolResult(Id, new Dictionary<string, object?>
            {
                ["manifest"] = ToolResultFactory.TruncateUtf8(joined, ctx.MaxBytesOut),
                ["count"] = lines.Count,
                ["truncated"] = Encoding.UTF8.GetByteCount(joined) > ctx.MaxBytesOut
            }, true);
        }
        catch (Exception ex)
        {
            return ToolResultFactory.Error(Id, "hash.dir_manifest_failed", ex.Message);
        }
    }
}
