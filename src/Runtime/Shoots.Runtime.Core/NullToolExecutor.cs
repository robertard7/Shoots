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

    public ToolResult Execute(ToolInvocation invocation)
    {
        if (invocation is null)
            throw new ArgumentNullException(nameof(invocation));

        return new ToolResult(invocation.ToolId, _outputs, _success);
    }
}
