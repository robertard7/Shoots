using System.Windows;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IToolTierPrompt
{
    bool ConfirmSystemTier(UiToolpackTier currentTier);
}

public sealed class ToolTierPrompt : IToolTierPrompt
{
    public bool ConfirmSystemTier(UiToolpackTier currentTier)
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
    public bool ConfirmSystemTier(UiToolpackTier currentTier)
    {
        _ = currentTier;
        return true;
    }
}
