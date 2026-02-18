using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions.Execution;

public enum ExecutionResultKind
{
    ToolExecuted,
    DecisionRequired,
    Failed
}

public sealed record DecisionRequest(
    string RequestId,
    string RouteGateId,
    IReadOnlyDictionary<string, object?> Context
);

public sealed record ExecutionResult(
    string RequestId,
    ExecutionResultKind Kind,
    ToolResult? ToolResult,
    DecisionRequest? DecisionRequest,
    string? ErrorCode,
    string? ErrorMessage
);
