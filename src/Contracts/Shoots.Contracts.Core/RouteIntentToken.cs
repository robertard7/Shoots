using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoots.Contracts.Core;

/// <summary>
/// Immutable intent token carried through routing decisions.
/// </summary>
public sealed record RouteIntentToken(
    WorkOrderId WorkOrderId,
    string CurrentNodeId,
    IReadOnlyList<string> AllowedNextNodeIds,
    string GraphStructureHash
)
{
    public bool Equals(RouteIntentToken? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return WorkOrderId.Equals(other.WorkOrderId)
               && string.Equals(CurrentNodeId, other.CurrentNodeId, StringComparison.Ordinal)
               && string.Equals(GraphStructureHash, other.GraphStructureHash, StringComparison.Ordinal)
               && AllowedNextNodeIds.SequenceEqual(other.AllowedNextNodeIds, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WorkOrderId);
        hash.Add(CurrentNodeId, StringComparer.Ordinal);
        foreach (var nodeId in AllowedNextNodeIds)
            hash.Add(nodeId, StringComparer.Ordinal);
        hash.Add(GraphStructureHash, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
