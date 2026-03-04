using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.Contracts.Core;

public sealed record RepoSliceRequest
{
    public string Root { get; init; } = string.Empty;
    public IReadOnlyList<string> IncludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = Array.Empty<string>();
    public int MaxFiles { get; init; } = 256;
    public int MaxBytesPerFile { get; init; } = 16 * 1024;
    public int MaxTotalBytes { get; init; } = 256 * 1024;
    public bool AllowBinary { get; init; }
    public int LineCap { get; init; } = 400;
    public bool NormalizeEol { get; init; } = true;

    public RepoSliceRequest Normalize() => this with
    {
        Root = Root.Replace('\\', '/'),
        IncludeGlobs = IncludeGlobs.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        ExcludeGlobs = ExcludeGlobs.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray()
    };

    public string ComputeInputsHash()
    {
        var normalized = Normalize();
        var payload = JsonSerializer.Serialize(normalized, RepoSliceJson.Options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record RepoSliceFile
{
    public string RelPath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int Bytes { get; init; }
    public int Lines { get; init; }
    public string MimeHint { get; init; } = "text/plain";
    public string Excerpt { get; init; } = string.Empty;
    public bool Truncated { get; init; }

    public RepoSliceFile Normalize() => this with { RelPath = RelPath.Replace('\\', '/') };
}

public sealed record RepoSliceStats
{
    public int SelectedFiles { get; init; }
    public int SelectedBytes { get; init; }
    public int TruncatedFiles { get; init; }
    public int RejectedBinaryFiles { get; init; }
}

public sealed record RepoSliceResult
{
    public string SliceId { get; init; } = string.Empty;
    public string InputsHash { get; init; } = string.Empty;
    public IReadOnlyList<RepoSliceFile> Files { get; init; } = Array.Empty<RepoSliceFile>();
    public IReadOnlyList<string> TruncationFlags { get; init; } = Array.Empty<string>();
    public RepoSliceStats Stats { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public RepoSliceResult Normalize() => this with
    {
        Files = Files.Select(x => x.Normalize()).OrderBy(x => x.RelPath, StringComparer.Ordinal).ToArray(),
        TruncationFlags = TruncationFlags.OrderBy(x => x, StringComparer.Ordinal).ToArray()
    };
}

public static class RepoSliceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
