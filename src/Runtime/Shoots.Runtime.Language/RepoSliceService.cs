using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Language;

public sealed class RepoSliceService
{
    public RepoSliceResult BuildSlice(RepoSliceRequest request)
    {
        var normalized = request.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.Root) || !Directory.Exists(normalized.Root))
        {
            return Error(normalized, "slice.root.missing", "Slice root does not exist.");
        }

        Regex[] includes;
        Regex[] excludes;
        try
        {
            includes = Compile(normalized.IncludeGlobs, fallbackMatchAll: true);
            excludes = Compile(normalized.ExcludeGlobs, fallbackMatchAll: false);
        }
        catch (ArgumentException ex)
        {
            return Error(normalized, "slice.pattern.invalid", ex.Message);
        }

        var files = new List<RepoSliceFile>();
        var truncationFlags = new SortedSet<string>(StringComparer.Ordinal);
        var selectedBytes = 0;
        var truncatedFiles = 0;
        var rejectedBinary = 0;

        foreach (var path in Directory.EnumerateFiles(normalized.Root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relPath = Path.GetRelativePath(normalized.Root, path).Replace('\\', '/');
            if (!IsMatch(includes, relPath) || IsMatch(excludes, relPath))
            {
                continue;
            }

            if (files.Count >= normalized.MaxFiles)
            {
                truncationFlags.Add("slice.cap.exceeded.max_files");
                break;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                return Error(normalized, "slice.read.failed", $"{relPath}: {ex.Message}");
            }

            if (!normalized.AllowBinary && bytes.Contains((byte)0))
            {
                rejectedBinary++;
                truncationFlags.Add("slice.binary.disallowed");
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (normalized.NormalizeEol)
            {
                text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            }

            var lines = text.Split('\n');
            var truncated = false;
            if (lines.Length > normalized.LineCap)
            {
                lines = lines[..normalized.LineCap];
                truncated = true;
                truncationFlags.Add("slice.truncated.line_cap");
            }

            var excerpt = string.Join("\n", lines);
            var excerptBytes = Encoding.UTF8.GetByteCount(excerpt);
            if (excerptBytes > normalized.MaxBytesPerFile)
            {
                excerpt = TruncateAtBoundary(excerpt, normalized.MaxBytesPerFile);
                excerptBytes = Encoding.UTF8.GetByteCount(excerpt);
                truncated = true;
                truncationFlags.Add("slice.truncated.bytes_per_file");
            }

            if (selectedBytes + excerptBytes > normalized.MaxTotalBytes)
            {
                truncationFlags.Add("slice.cap.exceeded.total_bytes");
                break;
            }

            selectedBytes += excerptBytes;
            if (truncated)
            {
                truncatedFiles++;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(excerpt))).ToLowerInvariant();
            files.Add(new RepoSliceFile
            {
                RelPath = relPath,
                Sha256 = hash,
                Bytes = excerptBytes,
                Lines = lines.Length,
                MimeHint = GuessMime(relPath),
                Excerpt = excerpt,
                Truncated = truncated
            });
        }

        var normalizedFiles = files.OrderBy(f => f.RelPath, StringComparer.Ordinal).ToArray();
        var inputsHash = normalized.ComputeInputsHash();
        var sliceIdentity = string.Join("\n", normalizedFiles.Select(f => $"{f.RelPath}:{f.Sha256}:{f.Bytes}:{f.Lines}"));
        var sliceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{inputsHash}\n{sliceIdentity}"))).ToLowerInvariant();

        return new RepoSliceResult
        {
            SliceId = sliceId,
            InputsHash = inputsHash,
            Files = normalizedFiles,
            TruncationFlags = truncationFlags.ToArray(),
            Stats = new RepoSliceStats
            {
                SelectedFiles = normalizedFiles.Length,
                SelectedBytes = selectedBytes,
                TruncatedFiles = truncatedFiles,
                RejectedBinaryFiles = rejectedBinary
            }
        }.Normalize();
    }

    private static RepoSliceResult Error(RepoSliceRequest request, string errorCode, string message)
        => new()
        {
            SliceId = string.Empty,
            InputsHash = request.ComputeInputsHash(),
            ErrorCode = errorCode,
            ErrorMessage = message,
            Files = Array.Empty<RepoSliceFile>(),
            TruncationFlags = Array.Empty<string>(),
            Stats = new RepoSliceStats()
        };

    private static Regex[] Compile(IReadOnlyList<string> globs, bool fallbackMatchAll)
    {
        if (globs.Count == 0)
        {
            return fallbackMatchAll ? new[] { new Regex("^.*$", RegexOptions.Compiled | RegexOptions.CultureInvariant) } : Array.Empty<Regex>();
        }

        return globs.Select(g => new Regex("^" + Regex.Escape(g).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant)).ToArray();
    }

    private static bool IsMatch(IEnumerable<Regex> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(value))
            {
                return true;
            }
        }

        return false;
    }

    private static string TruncateAtBoundary(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytesSoFar = 0;
        var builder = new StringBuilder();
        foreach (var line in value.Split('\n'))
        {
            var candidate = builder.Length == 0 ? line : "\n" + line;
            var candidateBytes = Encoding.UTF8.GetByteCount(candidate);
            if (bytesSoFar + candidateBytes > maxBytes)
            {
                break;
            }

            builder.Append(candidate);
            bytesSoFar += candidateBytes;
        }

        return builder.ToString();
    }

    private static string GuessMime(string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext switch
        {
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".cs" => "text/x-csharp",
            ".yml" or ".yaml" => "text/yaml",
            ".xml" => "application/xml",
            _ => "text/plain"
        };
    }
}
