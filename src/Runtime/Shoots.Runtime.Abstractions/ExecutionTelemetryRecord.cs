namespace Shoots.Runtime.Abstractions;

public sealed record ExecutionTelemetryRecord(
    int Tick,
    RoutingTraceEventKind Event,
    string? Detail = null
);
