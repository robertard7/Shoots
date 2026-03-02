#nullable enable

using System.Collections.ObjectModel;

namespace Shoots.Ui.ViewModels;

public sealed class ToolExecutionSessionViewModel
{
    public string SessionId { get; set; } = string.Empty;
    public ObservableCollection<ToolExecutionRecordViewModel> Records { get; } = new();
}