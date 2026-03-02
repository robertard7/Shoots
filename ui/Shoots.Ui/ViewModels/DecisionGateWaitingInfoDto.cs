#nullable enable

using System.Collections.Generic;

namespace Shoots.UI.ViewModels;

/// <summary>
/// UI-safe waiting gate DTO.
/// Do NOT depend on host/runtime types here.
/// </summary>
public sealed record DecisionGateWaitingInfoDto(
    string ReasonCode,
    string RouteGateId,
    string CurrentNodeId,
    string DecisionPromptKey,
    string Policy,
    bool FallbackPresent,
    IReadOnlyList<string> AllowedNextNodes,
    string PlanHash,
    string WorkOrderId)
{
    public string GateDecision => Policy;
}