using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Read-only context for AI decision requests.
/// </summary>
public sealed record AiDecisionRequest(
    WorkOrder WorkOrder,
    RouteStep RouteStep,
    string GraphHash,
    string CatalogHash,
    ToolCatalogSnapshot Catalog
);
