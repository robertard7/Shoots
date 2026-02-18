using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions.Execution;

public enum ExecutionEnvelopeKind
{
    Tool,
    Decision
}

public sealed record ExecutionEnvelope(
    string RequestId,
    ExecutionEnvelopeKind Kind,
    ToolId? ToolId,
    IReadOnlyDictionary<string, object?> Args,
    string? InputText,
    string? RouteGateId,
    IReadOnlyDictionary<string, object?> Context
);
