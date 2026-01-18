using System;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullAiDecisionProvider : IAiDecisionProvider
{
    public static readonly NullAiDecisionProvider Instance = new();

    public ToolSelectionDecision? RequestDecision(AiDecisionRequestContext context)
    {
        _ = context ?? throw new ArgumentNullException(nameof(context));

        return null;
    }
}
