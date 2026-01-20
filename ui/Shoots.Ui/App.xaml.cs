using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Shoots.Ui;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Shoots.Ui.SingleInstance";
        var createdNew = false;
        _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        MainWindow = new MainWindow();
        MainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        var errorWindow = new RuntimeErrorWindow(e.Exception);
        errorWindow.ShowDialog();

        Shutdown();
    }
}
