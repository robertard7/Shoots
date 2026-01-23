using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Shoots.Contracts.Core;

namespace Shoots.UI.ExecutionRecords;

public sealed class ToolExecutionSessionViewModel
{
    public ToolExecutionSessionViewModel(
        string sessionId,
        string planId,
        DateTimeOffset startedAt,
        bool isPreview,
        string label,
        IReadOnlyList<ToolExecutionRecordViewModel> records)
    {
        SessionId = sessionId;
        PlanId = planId;
        StartedAt = startedAt;
        IsPreview = isPreview;
        Label = label;
        Records = new ReadOnlyObservableCollection<ToolExecutionRecordViewModel>(
            new ObservableCollection<ToolExecutionRecordViewModel>(records));
    }

    public string SessionId { get; }

    public string PlanId { get; }

    public DateTimeOffset StartedAt { get; }

    public bool IsPreview { get; }

    public string Label { get; }

    public ReadOnlyObservableCollection<ToolExecutionRecordViewModel> Records { get; }

    public string DisplayName => $"{Label} · {StartedAt:u}";

    public static ToolExecutionSessionViewModel CreatePreview(
        BuildPlan plan,
        IReadOnlyList<ToolExecutionRecordViewModel> records)
    {
        return new ToolExecutionSessionViewModel(
            $"preview-{plan.PlanId}",
            plan.PlanId,
            DateTimeOffset.UtcNow,
            true,
            "Preview",
            records);
    }

    public static ToolExecutionSessionViewModel CreateRun(
        BuildPlan plan,
        DateTimeOffset startedAt,
        IReadOnlyList<ToolExecutionRecordViewModel> records)
    {
        return new ToolExecutionSessionViewModel(
            $"run-{startedAt:yyyyMMddHHmmss}",
            plan.PlanId,
            startedAt,
            false,
            "Run",
            records);
    }

    public static ToolExecutionSessionViewModel CreateReplay(
        BuildPlan plan,
        IReadOnlyList<ToolExecutionRecordViewModel> records)
    {
        return new ToolExecutionSessionViewModel(
            $"replay-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            plan.PlanId,
            DateTimeOffset.UtcNow,
            true,
            "Replay",
            records);
    }
}
