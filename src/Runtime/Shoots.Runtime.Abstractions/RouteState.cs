using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Immutable routing state snapshot.
/// </summary>
public sealed record RouteState(
    WorkOrder WorkOrder,
    int StepIndex,
    ToolSelection? ToolSelection = null
);
