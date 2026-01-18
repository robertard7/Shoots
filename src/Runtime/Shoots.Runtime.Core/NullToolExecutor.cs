using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullToolExecutor : IToolExecutor
{
    private readonly bool _success;
    private readonly IReadOnlyDictionary<string, object?> _outputs;

    public NullToolExecutor(bool success = true, IReadOnlyDictionary<string, object?>? outputs = null)
    {
        _success = success;
        _outputs = outputs ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public ToolResult Execute(ToolInvocation invocation, ExecutionEnvelope envelope)
    {
        if (invocation is null)
            throw new ArgumentNullException(nameof(invocation));
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));

        return new ToolResult(invocation.ToolId, _outputs, _success);
    }
}
