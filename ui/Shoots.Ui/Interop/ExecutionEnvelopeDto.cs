#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoots.UI.Interop;

/// <summary>
/// UI-safe execution snapshot DTO. No runtime assembly dependency.
/// </summary>
public sealed record ExecutionEnvelopeDto(
    string ExecutionId,
    ExecutionTraceDto Trace,
    IReadOnlyList<ExecutionArtifactDto> Artifacts)
{
    public string GetExecutionId()
        => string.IsNullOrWhiteSpace(ExecutionId) ? "unknown-exec" : ExecutionId;
}

public sealed record ExecutionTraceDto(IReadOnlyList<ExecutionTraceEntryDto> Entries);

public sealed record ExecutionTraceEntryDto(int Tick, string Event, string? Detail);

public sealed record ExecutionArtifactDto(string Id, string Description)
{
    public string SafeId => string.IsNullOrWhiteSpace(Id) ? "artifact" : Id;
    public string SafeDescription => string.IsNullOrWhiteSpace(Description) ? "(no description)" : Description;
}

/// <summary>
/// Minimal helper so callers can build a stable DTO even if they have messy inputs.
/// </summary>
public static class ExecutionEnvelopeDtoFactory
{
    public static ExecutionEnvelopeDto Create(
        string executionId,
        IEnumerable<(int tick, string evt, string? detail)> trace,
        IEnumerable<(string id, string description)> artifacts)
    {
        var entries = trace
            .Select(t => new ExecutionTraceEntryDto(t.tick, t.evt ?? string.Empty, t.detail))
            .ToList();

        var arts = artifacts
            .Select(a => new ExecutionArtifactDto(a.id ?? string.Empty, a.description ?? string.Empty))
            .ToList();

        return new ExecutionEnvelopeDto(
            executionId ?? string.Empty,
            new ExecutionTraceDto(entries),
            arts);
    }
}