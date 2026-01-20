using System;
using System.Collections.Generic;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public static class ReplayLoader
{
    public static ReplaySummary BuildSummary(RoutingTrace trace)
    {
        if (trace is null)
            throw new ArgumentNullException(nameof(trace));

        var entries = new List<ReplaySummaryEntry>();

        foreach (var entry in trace.Entries)
        {
            if (entry.Event == RoutingTraceEventKind.DecisionRequired &&
                entry.Detail is not null &&
                entry.Detail.Contains("provider.invoked", StringComparison.Ordinal))
            {
                var decisionId = GetDetailValue(entry.Detail, "decision.id");
                entries.Add(new ReplaySummaryEntry(entry.Tick, ReplaySummaryEntryKind.ProviderDecision, decisionId));
                continue;
            }

            if (entry.Event == RoutingTraceEventKind.Error &&
                entry.Detail is not null &&
                entry.Detail.StartsWith("provider.failure", StringComparison.Ordinal))
            {
                var kindValue = GetDetailValue(entry.Detail, "kind");
                var providerId = GetDetailValue(entry.Detail, "provider");
                var kind = TryParseFailureKind(kindValue);
                entries.Add(new ReplaySummaryEntry(
                    entry.Tick,
                    ReplaySummaryEntryKind.ProviderFailure,
                    ProviderId: providerId,
                    FailureKind: kind));
                continue;
            }

            if (entry.Event == RoutingTraceEventKind.ToolExecuted &&
                entry.Detail is not null &&
                entry.Detail.StartsWith("tool.id=", StringComparison.Ordinal))
            {
                var toolId = GetDetailValue(entry.Detail, "tool.id");
                entries.Add(new ReplaySummaryEntry(entry.Tick, ReplaySummaryEntryKind.ToolSelection, ToolId: toolId));
            }
        }

        return new ReplaySummary(entries);
    }

    private static ProviderFailureKind? TryParseFailureKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<ProviderFailureKind>(value, out var parsed) ? parsed : null;
    }

    private static string? GetDetailValue(string detail, string key)
    {
        var segments = detail.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var segmentKey = segment[..separator];
            if (!string.Equals(segmentKey, key, StringComparison.Ordinal))
                continue;

            return separator == segment.Length - 1 ? string.Empty : segment[(separator + 1)..];
        }

        return null;
    }
}
