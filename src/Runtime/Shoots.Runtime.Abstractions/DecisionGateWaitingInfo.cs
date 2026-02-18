using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public sealed record DecisionGateWaitingInfo(
    WorkOrderId WorkOrderId,
    string RouteGateId,
    string CurrentNodeId,
    string IntentTokenHash,
    IReadOnlyList<string> AllowedNextNodes,
    string DecisionPromptKey,
    DecisionOwner DecisionOwner,
    DecisionPolicy Policy,
    FallbackToolSelection? FallbackToolSelection,
    string? FallbackNextNodeId
);
