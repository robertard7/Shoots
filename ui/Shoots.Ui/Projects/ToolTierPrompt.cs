using System.Windows;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IToolTierPrompt
{
    bool ConfirmSystemTier(ToolpackTier currentTier);
}

public sealed class ToolTierPrompt : IToolTierPrompt
{
    public bool ConfirmSystemTier(ToolpackTier currentTier)
    {
        var message = "System tier surfaces OS-level tool stubs. Enable System tier?";
        var title = $"Enable System Tier (current: {currentTier})";
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }
}

public sealed class NoOpToolTierPrompt : IToolTierPrompt
{
    public bool ConfirmSystemTier(ToolpackTier currentTier)
    {
        _ = currentTier;
        return true;
    }
}
