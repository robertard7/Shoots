using System;
using System.Windows;

namespace Shoots.UI;

public partial class FatalErrorWindow : Window
{
    public FatalErrorWindow(Exception exception, string? logPath)
    {
        InitializeComponent();
        DataContext = new FatalErrorViewModel(exception, logPath);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private sealed class FatalErrorViewModel
    {
        public FatalErrorViewModel(Exception exception, string? logPath)
        {
            ErrorText = exception.ToString();
            LogPath = string.IsNullOrWhiteSpace(logPath) ? "Unavailable" : logPath;
        }

        public string ErrorText { get; }

        public string LogPath { get; }
    }
}
