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
    private readonly BuildPlan _plan;
    private readonly string _catalogHash;

    public RoutingTraceBuilder(BuildPlan plan, string catalogHash, RoutingTrace? seed = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _catalogHash = string.IsNullOrWhiteSpace(catalogHash)
            ? throw new ArgumentException("catalog hash is required", nameof(catalogHash))
            : catalogHash;

        if (seed is not null)
        {
            _entries.AddRange(seed.Entries);
            _nextTick = seed.Entries.Count == 0 ? 0 : seed.Entries.Max(entry => entry.Tick) + 1;
        }
    }

    public RoutingTrace Build() => new RoutingTrace(_plan, _catalogHash, _entries.ToArray());

    public IReadOnlyList<ExecutionTelemetryRecord> BuildTelemetry()
    {
        return _entries
            .Select(entry => new ExecutionTelemetryRecord(entry.Tick, entry.Event, entry.Detail))
            .ToArray();
    }

    public void Add(
        RoutingTraceEventKind kind,
        string? detail = null,
        string? fromNodeId = null,
        string? toNodeId = null,
        RoutingDecisionSource? decisionSource = null,
        RoutingState? state = null,
        RouteStep? step = null,
        RuntimeError? error = null)
    {
        var tick = _nextTick++;
        if (error is not null)
        {
            var correlationId = string.IsNullOrWhiteSpace(error.CorrelationId)
                ? RuntimeError.CreateCorrelationId(tick, error.Code)
                : error.CorrelationId;
            if (!string.Equals(error.CorrelationId, correlationId, StringComparison.Ordinal))
                error = error with { CorrelationId = correlationId };
            detail = EnsureCorrelationDetail(detail, correlationId);
        }

        _entries.Add(new RoutingTraceEntry(tick, kind, detail, fromNodeId, toNodeId, decisionSource, state, step, error));
    }

    private static string EnsureCorrelationDetail(string? detail, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return detail ?? string.Empty;

        if (detail is null)
            return $"correlation={correlationId}";

        if (detail.Contains("correlation=", StringComparison.Ordinal))
            return detail;

        return $"{detail}|correlation={correlationId}";
    }
}
