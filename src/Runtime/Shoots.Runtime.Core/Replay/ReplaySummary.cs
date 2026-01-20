using System.Collections.Generic;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed record ReplaySummary(IReadOnlyList<ReplaySummaryEntry> Entries);

public sealed record ReplaySummaryEntry(
    int Tick,
    ReplaySummaryEntryKind Kind,
    string? DecisionId = null,
    string? ProviderId = null,
    ProviderFailureKind? FailureKind = null,
    string? ToolId = null);

public enum ReplaySummaryEntryKind
{
    ProviderDecision,
    ProviderFailure,
    ToolSelection
}
