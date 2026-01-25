using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.ExecutionRecords;

namespace Shoots.UI.AiHelp;

public sealed class ToolExecutionCatalogAiHelpSurface : IAiHelpSurface
{
    private readonly IReadOnlyCollection<ToolExecutionSessionViewModel> _sessions;
    private readonly IReadOnlyCollection<ToolExecutionRecordViewModel> _records;
    private readonly IReadOnlyCollection<ToolExecutionRecordViewModel> _comparisonRecords;

    public ToolExecutionCatalogAiHelpSurface(
        IReadOnlyCollection<ToolExecutionSessionViewModel> sessions,
        IReadOnlyCollection<ToolExecutionRecordViewModel> records,
        IReadOnlyCollection<ToolExecutionRecordViewModel> comparisonRecords)
    {
        _sessions = sessions;
        _records = records;
        _comparisonRecords = comparisonRecords;
    }

    public string SurfaceId => "tool-executions";

    public string SurfaceKind => "Tool Execution History";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.ToolExecution),
        new AiIntentDescriptor(AiIntentType.Diagnose, AiIntentScope.ToolExecution),
        new AiIntentDescriptor(AiIntentType.Compare, AiIntentScope.ToolExecution)
    };

    public string DescribeContext()
        => $"{_sessions.Count} sessions loaded with {_records.Count} records selected.";

    public string DescribeCapabilities()
        => $"Comparison records available: {_comparisonRecords.Count}.";

    public string DescribeConstraints()
        => "Tool execution records are read-only.";
}
