using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class DeterministicToolExecutor : IToolExecutor
{
    private readonly IToolRegistry _registry;

    public DeterministicToolExecutor(IToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ToolResult Execute(ToolInvocation invocation, ExecutionEnvelope envelope)
    {
        if (invocation is null)
            throw new ArgumentNullException(nameof(invocation));
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));

        var snapshot = _registry.GetSnapshot();
        var entry = snapshot.FirstOrDefault(candidate => candidate.Spec.ToolId == invocation.ToolId);
        if (entry is null)
        {
            return Failure(invocation.ToolId, "tool_missing");
        }

        if (!ToolAuthorityValidator.TryValidateAuthority(envelope.Plan.Authority, entry.Spec.RequiredAuthority, out var error))
            return Failure(invocation.ToolId, error?.Code ?? "tool_authority_denied");

        if (invocation.Bindings is null)
            return Failure(invocation.ToolId, "tool_bindings_missing");

        foreach (var input in entry.Spec.Inputs)
        {
            if (input.Required && !invocation.Bindings.ContainsKey(input.Name))
                return Failure(invocation.ToolId, "tool_binding_missing");
        }

        foreach (var binding in invocation.Bindings.Keys)
        {
            if (entry.Spec.Inputs.All(input => !string.Equals(input.Name, binding, StringComparison.Ordinal)))
                return Failure(invocation.ToolId, "tool_binding_unknown");
        }

        var outputs = new Dictionary<string, object?>
        {
            ["tool.id"] = invocation.ToolId.Value,
            ["catalog.hash"] = _registry.CatalogHash
        };

        return new ToolResult(invocation.ToolId, outputs, true);
    }

    private static ToolResult Failure(ToolId toolId, string code)
    {
        var outputs = new Dictionary<string, object?>
        {
            ["error.code"] = code
        };

        return new ToolResult(toolId, outputs, false);
    }
}
