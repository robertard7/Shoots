using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.ExecutionRecords;

public sealed class ToolExecutionRecordViewModel : IAiHelpSurface
{
    public ToolExecutionRecordViewModel(
        string recordId,
        string toolId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        bool? success,
        string? errorMessage,
        DateTimeOffset capturedAt)
    {
        RecordId = recordId;
        ToolId = toolId;
        Inputs = inputs;
        Outputs = outputs;
        Success = success;
        ErrorMessage = errorMessage;
        CapturedAt = capturedAt;
    }

    public string RecordId { get; }

    public string ToolId { get; }

    public IReadOnlyDictionary<string, object?> Inputs { get; }

    public IReadOnlyDictionary<string, object?> Outputs { get; }

    public bool? Success { get; }

    public string? ErrorMessage { get; }

    public DateTimeOffset CapturedAt { get; }

    public string DisplayName => $"{ToolId} · {RecordId}";

    public string InputsSummary => FormatKeyValues(Inputs, "No inputs recorded.");

    public string OutputsSummary => FormatKeyValues(Outputs, "No outputs recorded.");

    public string ErrorSummary => Success switch
    {
        true => "No errors recorded.",
        false => string.IsNullOrWhiteSpace(ErrorMessage) ? "Recorded failure." : ErrorMessage,
        _ => "Result is not recorded."
    };

    public string SurfaceKind => $"Tool execution {ToolId}";

    public string DescribeContext()
        => $"Tool '{ToolId}' recorded at {CapturedAt:u}.";

    public string DescribeCapabilities()
        => $"Inputs: {FormatKeyValues(Inputs, "none")}. Outputs: {FormatKeyValues(Outputs, "none")}.";

    public string DescribeConstraints()
        => $"Result status: {FormatStatus()}.";

    public static ToolExecutionRecordViewModel FromPlanStep(ToolBuildStep step, ToolResult? result)
    {
        var outputValues = result is not null && result.ToolId == step.ToolId
            ? result.Outputs
            : new Dictionary<string, object?>();

        var success = result is not null && result.ToolId == step.ToolId
            ? result.Success
            : null;

        var error = success == false ? "Recorded output indicates a failure." : null;

        return new ToolExecutionRecordViewModel(
            step.Id,
            step.ToolId.Value,
            step.InputBindings,
            outputValues,
            success,
            error,
            DateTimeOffset.UtcNow);
    }

    public string FormatDiff(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right, string label)
    {
        var allKeys = left.Keys
            .Union(right.Keys)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var diffs = new List<string>();
        foreach (var key in allKeys)
        {
            left.TryGetValue(key, out var leftValue);
            right.TryGetValue(key, out var rightValue);
            if (Equals(leftValue, rightValue))
                continue;

            diffs.Add($"{key}: {FormatValue(leftValue)} → {FormatValue(rightValue)}");
        }

        if (diffs.Count == 0)
            return $"{label}: no changes.";

        return $"{label} changes: {string.Join(", ", diffs)}.";
    }

    public string FormatStatusDiff(ToolExecutionRecordViewModel other)
    {
        if (Equals(Success, other.Success) && string.Equals(ErrorMessage, other.ErrorMessage, StringComparison.Ordinal))
            return "Status: no changes.";

        return $"Status: {FormatStatus()} → {other.FormatStatus()}.";
    }

    private static string FormatKeyValues(IReadOnlyDictionary<string, object?> values, string emptyLabel)
    {
        if (values.Count == 0)
            return emptyLabel;

        return string.Join(", ", values.Select(pair => $"{pair.Key}={FormatValue(pair.Value)}"));
    }

    private string FormatStatus()
    {
        return Success switch
        {
            true => "success",
            false => "failure",
            _ => "unknown"
        };
    }

    private static string FormatValue(object? value)
        => value?.ToString() ?? "null";
}
