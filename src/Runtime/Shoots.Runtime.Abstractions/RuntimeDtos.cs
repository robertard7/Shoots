using System.Collections.Generic;

namespace Shoots.Runtime.Abstractions;

public sealed record ExecutionEnvelopeDto(
    string PlanId,
    ExecutionFinalStatus FinalStatus,
    IReadOnlyList<ToolResultDto> ToolResults,
    IReadOnlyList<BuildArtifactDto> Artifacts,
    RoutingTraceDto Trace
);

public sealed record RoutingTraceDto(
    string PlanId,
    string CatalogHash,
    IReadOnlyList<RoutingTraceEntryDto> Entries
);

public sealed record RoutingTraceEntryDto(
    int Tick,
    RoutingTraceEventKind Event,
    string? Detail = null
);

public sealed record ToolResultDto(
    string ToolId,
    bool Success,
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<string> Tags
);

public sealed record BuildArtifactDto(
    string Id,
    string Description
);
