namespace Shoots.Tools.Abstractions;

public interface IDeterministicClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemDeterministicClock : IDeterministicClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record ToolExecutionContext(
    string RepoRoot,
    string WorkingDirectory,
    int MaxBytesOut,
    int MaxTimeoutMs,
    bool AllowNetwork,
    bool AllowPrivileged,
    IDictionary<string, string?> EnvOverlay,
    CancellationToken CancellationToken,
    IDeterministicClock Clock)
{
    public static ToolExecutionContext Create(
        string repoRoot,
        CancellationToken ct,
        int maxBytesOut = 16384,
        int maxTimeoutMs = 30000,
        bool allowNetwork = false,
        bool allowPrivileged = false)
        => new(
            repoRoot,
            repoRoot,
            maxBytesOut,
            maxTimeoutMs,
            allowNetwork,
            allowPrivileged,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            ct,
            new SystemDeterministicClock());
}
