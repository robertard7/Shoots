namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable work order identifier.
/// </summary>
public readonly record struct WorkOrderId(string Value);

/// <summary>
/// Immutable user intent capsule.
/// Reissue the work order to change any value.
/// </summary>
public sealed record WorkOrder(
    WorkOrderId Id,
    string OriginalRequest,
    string Goal,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> SuccessCriteria
);
