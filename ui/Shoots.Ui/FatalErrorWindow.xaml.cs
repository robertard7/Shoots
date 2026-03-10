using System;
using System.IO;
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
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            ExceptionMessage = string.IsNullOrWhiteSpace(exception.Message) ? "No message available." : exception.Message;
            PrimaryStackFrame = ExtractPrimaryStackLine(exception);
            LogPath = string.IsNullOrWhiteSpace(logPath) ? "Unavailable" : logPath;
            UiLogPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Shoots.UI",
                "ui.log");
            ErrorText = exception.ToString();
        }

        public string ExceptionType { get; }

        public string ExceptionMessage { get; }

        public string PrimaryStackFrame { get; }

        public string ErrorText { get; }

        public string LogPath { get; }

        public string UiLogPath { get; }

        private static string ExtractPrimaryStackLine(Exception exception)
        {
            var trace = exception.StackTrace;
            if (string.IsNullOrWhiteSpace(trace))
            {
                return "No stack trace available.";
            }

            foreach (var line in trace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            return "No stack trace available.";
        }
    }
}
