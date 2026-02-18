using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions.Provider;

public enum ProviderExecutionResultKind
{
    ToolExecuted,
    DecisionRequired,
    Failed
}

public sealed record ProviderDecisionRequest(
    string RequestId,
    string RouteGateId,
    IReadOnlyDictionary<string, object?> Context
);

public sealed record ProviderExecutionResult(
    string RequestId,
    ProviderExecutionResultKind Kind,
    ToolResult? ToolResult,
    ProviderDecisionRequest? DecisionRequest,
    string? ErrorCode,
    string? ErrorMessage
);
