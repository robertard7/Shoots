using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Read-only routing observation for narrators.
/// </summary>
public sealed record RouteNarration(
    WorkOrder WorkOrder,
    RouteStep? CurrentStep,
    bool DecisionRequired,
    RuntimeError? HaltReason
);
