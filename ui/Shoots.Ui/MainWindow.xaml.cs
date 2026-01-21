using System.Windows;
using Shoots.UI.Services;
using Shoots.UI.ViewModels;

namespace Shoots.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(new NullExecutionCommandService());
    }
}
