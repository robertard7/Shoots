using System;
using System.Collections.Generic;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class InMemoryRuntimePersistence : IRuntimePersistence
{
    private readonly Dictionary<string, ExecutionEnvelope> _store =
        new(StringComparer.Ordinal);

    public void Save(ExecutionEnvelope envelope)
    {
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));

        _store[envelope.Plan.PlanId] = envelope;
    }

    public ExecutionEnvelope? Load(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return null;

        return _store.TryGetValue(planId, out var envelope)
            ? envelope
            : null;
    }
}
