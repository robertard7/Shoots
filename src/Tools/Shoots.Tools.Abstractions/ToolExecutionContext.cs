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
    bool AllowNetwork,
    CancellationToken CancellationToken,
    IDeterministicClock Clock)
{
    public static ToolExecutionContext Create(
        string repoRoot,
        CancellationToken ct,
        int maxBytesOut = 16384,
        bool allowNetwork = false)
        => new(
            repoRoot,
            repoRoot,
            maxBytesOut,
            allowNetwork,
            ct,
            new SystemDeterministicClock());
}
