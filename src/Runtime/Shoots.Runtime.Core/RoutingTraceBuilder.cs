using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

internal sealed class RoutingTraceBuilder
{
    private readonly List<RoutingTraceEntry> _entries = new();
    private int _nextTick;

    public RoutingTraceBuilder(RoutingTrace? seed = null)
    {
        if (seed is not null)
        {
            _entries.AddRange(seed.Entries);
            _nextTick = seed.Entries.Count == 0 ? 0 : seed.Entries.Max(entry => entry.Tick) + 1;
        }
    }

    public RoutingTrace Build() => new RoutingTrace(_entries.ToArray());

    public IReadOnlyList<ExecutionTelemetryRecord> BuildTelemetry()
    {
        return _entries
            .Select(entry => new ExecutionTelemetryRecord(entry.Tick, entry.Event, entry.Detail))
            .ToArray();
    }

    public void Add(
        RoutingTraceEventKind kind,
        string? detail = null,
        RoutingState? state = null,
        RouteStep? step = null,
        RuntimeError? error = null)
    {
        _entries.Add(new RoutingTraceEntry(_nextTick++, kind, detail, state, step, error));
    }
}
