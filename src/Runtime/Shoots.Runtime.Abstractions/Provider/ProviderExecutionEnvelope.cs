using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions.Provider;

public enum ProviderExecutionEnvelopeKind
{
    Tool,
    Decision
}

public sealed record ProviderExecutionEnvelope(
    string RequestId,
    ProviderExecutionEnvelopeKind Kind,
    ToolId? ToolId,
    IReadOnlyDictionary<string, object?> Args,
    string? InputText,
    string? RouteGateId,
    IReadOnlyDictionary<string, object?> Context
);
