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
        _store[BuildPlanIdentity.ComputePlanHash(envelope.Plan)] = envelope;
    }

    public ExecutionEnvelope? Load(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return null;

        return _store.TryGetValue(planId, out var envelope)
            ? envelope
            : null;
    }

    RunResumeState? IRunResumeStateStore.LoadByWorkOrderId(string workOrderId)
    {
        if (string.IsNullOrWhiteSpace(workOrderId))
            return null;

        return _resume.TryGetValue(workOrderId, out var state)
            ? state
            : null;
    }

    void IRunResumeStateStore.SaveByWorkOrderId(string workOrderId, RunResumeState state)
    {
        if (string.IsNullOrWhiteSpace(workOrderId))
            throw new ArgumentException("work order id is required", nameof(workOrderId));
        if (state is null)
            throw new ArgumentNullException(nameof(state));

        _resume[workOrderId] = state;
    }
}
