using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Read-only context for AI decision requests.
/// </summary>
public sealed record AiDecisionRequest(
    WorkOrder WorkOrder,
    string CurrentNodeId,
    MermaidNodeKind NodeKind,
    IReadOnlyList<string> AllowedNextNodes,
    ToolCatalogSnapshot Catalog
);
