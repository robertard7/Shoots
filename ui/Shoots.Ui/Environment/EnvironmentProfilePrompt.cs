using System.Windows;

namespace Shoots.UI.Environment;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class EnvironmentProfilePrompt : IEnvironmentProfilePrompt
{
    public bool ConfirmCapabilityChange(EnvironmentCapability previous, EnvironmentCapability next)
    {
        var message = "Applying this profile changes declared capabilities. Continue?";
        var result = MessageBox.Show(
            message,
            "Apply Environment Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }
}
