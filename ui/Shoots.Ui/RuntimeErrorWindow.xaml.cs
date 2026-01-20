using System;
using System.Windows;

namespace Shoots.Ui;

public partial class RuntimeErrorWindow : Window
{
    public RuntimeErrorWindow(Exception exception)
    {
        InitializeComponent();
        DataContext = new RuntimeErrorViewModel(exception);
    }

    private sealed class RuntimeErrorViewModel
    {
        public RuntimeErrorViewModel(Exception exception)
        {
            ErrorText = exception.ToString();
        }

        public string ErrorText { get; }
    }
}
