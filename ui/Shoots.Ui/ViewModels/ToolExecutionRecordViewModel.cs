#nullable enable

namespace Shoots.Ui.ViewModels;

public sealed class ToolExecutionRecordViewModel
{
    public string ToolId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}