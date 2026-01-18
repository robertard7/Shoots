using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Immutable snapshot of routing progress and tool results.
/// </summary>
public sealed record ExecutionEnvelope(
    BuildPlan Plan,
    RoutingState State,
    IReadOnlyList<ToolResult> ToolResults,
    IReadOnlyList<BuildArtifact> Artifacts,
    RoutingTrace Trace,
    IReadOnlyList<ExecutionTelemetryRecord> Telemetry,
    string ToolCatalogHash,
    ExecutionFinalStatus FinalStatus
);
