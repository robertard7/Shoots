using System;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullAiDecisionProvider : IAiDecisionProvider
{
    public static readonly NullAiDecisionProvider Instance = new();

    public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
    {
        _ = request ?? throw new ArgumentNullException(nameof(request));

        return null;
    }
}
