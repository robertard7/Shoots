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
        DecisionOwner owner,
        DateTimeOffset capturedAt)
    {
        RecordId = recordId;
        ToolId = toolId;
        Inputs = inputs;
        Outputs = outputs;
        Success = success;
        ErrorMessage = errorMessage;
        Owner = owner;
        CapturedAt = capturedAt;
    }

    public string RecordId { get; }

    public string ToolId { get; }

    public IReadOnlyDictionary<string, object?> Inputs { get; }

    public IReadOnlyDictionary<string, object?> Outputs { get; }

    public bool? Success { get; }

    public string? ErrorMessage { get; }

    public DecisionOwner Owner { get; }

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

    public string OwnerBadge => Owner switch
    {
        DecisionOwner.Ai => "AI",
        DecisionOwner.Human => "Human",
        DecisionOwner.Rule => "Rule",
        _ => "Runtime"
    };

    public string OwnerHighlight => Owner switch
    {
        DecisionOwner.Ai => "#FF2563EB",
        DecisionOwner.Human => "#FF16A34A",
        DecisionOwner.Rule => "#FF7C3AED",
        _ => "#FF6B7280"
    };

    public string SurfaceId => $"ter:{ToolId}:{RecordId}:{CapturedAt:yyyyMMddHHmmss}";

    public string SurfaceKind => $"Tool execution {ToolId}";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.ToolExecution),
        new AiIntentDescriptor(AiIntentType.Diagnose, AiIntentScope.ToolExecution),
        new AiIntentDescriptor(AiIntentType.Compare, AiIntentScope.ToolExecution)
    };

    public string DescribeContext()
        => $"Tool '{ToolId}' recorded at {CapturedAt:u}. Owner: {Owner}.";

    public string DescribeCapabilities()
        => $"Inputs: {FormatKeyValues(Inputs, "none")}. Outputs: {FormatKeyValues(Outputs, "none")}.";

    public string DescribeConstraints()
        => $"Result status: {FormatStatus()}.";

	public static ToolExecutionRecordViewModel FromPlanStep(
		ToolBuildStep step,
		ToolResult? result,
		DecisionOwner owner)
	{
		bool isMatchingResult =
			result is not null &&
			result.ToolId == step.ToolId;

		IReadOnlyDictionary<string, object?> outputs =
			isMatchingResult
				? result!.Outputs
				: new Dictionary<string, object?>();

		bool? success =
			isMatchingResult
				? result!.Success
				: (bool?)null;

		string? error =
			success == false
				? "Recorded output indicates a failure."
				: null;

		return new ToolExecutionRecordViewModel(
			recordId: step.Id,
			toolId: step.ToolId.Value,
			inputs: step.InputBindings,
			outputs: outputs,
			success: success,
			errorMessage: error,
			owner: owner,
			capturedAt: DateTimeOffset.UtcNow);
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
