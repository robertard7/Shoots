using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public sealed record DecisionGateWaitingInfo(
    WorkOrderId WorkOrderId,
    string RouteGateId,
    string CurrentNodeId,
    string IntentTokenHash,
    string PlanHash,
    DecisionPolicy Policy,
    bool FallbackPresent,
    string ReasonCode,
    IReadOnlyList<string> AllowedNextNodes,
    string DecisionPromptKey,
    DecisionOwner DecisionOwner,
    FallbackToolSelection? FallbackToolSelection,
    string? FallbackNextNodeId
);
