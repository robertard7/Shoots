using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed record RoutingLoopResult(
    RoutingState State,
    IReadOnlyList<ToolResult> ToolResults,
    RoutingTrace Trace,
    IReadOnlyList<ExecutionTelemetryRecord> Telemetry
);
