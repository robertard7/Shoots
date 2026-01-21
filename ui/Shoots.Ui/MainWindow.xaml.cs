using System.Windows;
using Shoots.UI.Environment;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.ViewModels;

namespace Shoots.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(
            new NullExecutionCommandService(),
            new EnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            new ProjectWorkspaceProvider());
    }
}
