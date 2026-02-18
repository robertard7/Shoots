using System;
using System.Collections.Generic;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class InMemoryRuntimePersistence : IRuntimePersistence, IRunResumeStateStore
{
    private readonly Dictionary<string, ExecutionEnvelope> _store =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunResumeState> _resume =
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

    RunResumeState? IRunResumeStateStore.Load(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return null;

        return _resume.TryGetValue(planId, out var state)
            ? state
            : null;
    }

    void IRunResumeStateStore.Save(string planId, RunResumeState state)
    {
        if (string.IsNullOrWhiteSpace(planId))
            throw new ArgumentException("plan id is required", nameof(planId));
        if (state is null)
            throw new ArgumentNullException(nameof(state));

        _resume[planId] = state;
    }
}
