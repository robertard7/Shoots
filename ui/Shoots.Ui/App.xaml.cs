using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Shoots.UI.AiHelp;
using Shoots.UI.Services;

namespace Shoots.UI;

// This UI reflects and documents state.
// It does not enforce policy, execute logic, validate tools, or control external systems.

public partial class App : Application
{
    private const string MutexName = "Shoots.UI.SingleInstance";
    private const string ActivateEventName = "Shoots.UI.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;
    private bool _fatalWindowShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        var createdNew = false;
        _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            Log("Single-instance enforcement: existing instance detected.");
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.Invoke(BringMainWindowToFront),
            null,
            Timeout.Infinite,
            true);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainWindow = new MainWindow();
        MainWindow.Show();

        var surfaceRegistry = AiSurfaceRegistry.Current;
        Log($"AI surface registry: {surfaceRegistry.DescribeRegistrations()}");

#if DEBUG
        var missingRequired = surfaceRegistry.GetMissingSurfaceIds(UiSurfaceCatalog.RequiredSurfaceIds);
        var missingOptional = surfaceRegistry.GetMissingSurfaceIds(UiSurfaceCatalog.OptionalSurfaceIds);
        var missingOptionalPrefixes = surfaceRegistry.GetMissingSurfacePrefixes(UiSurfaceCatalog.OptionalSurfaceIdPrefixes);
        Log($"AI surfaces missing (required): {FormatSurfaceList(missingRequired)}; missing (optional): {FormatSurfaceList(missingOptional)}; missing (optional prefixes): {FormatSurfaceList(missingOptionalPrefixes)}");

        surfaceRegistry.AssertRequiredSurfacesRegistered(UiSurfaceCatalog.RequiredSurfaceIds);
#endif

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _activateWaitHandle?.Unregister(null);
        _activateWaitHandle = null;

        _activateEvent?.Dispose();
        _activateEvent = null;

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log("Single-instance enforcement: activate event not found.");
        }
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is null)
            return;

        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;

        MainWindow.Show();
        MainWindow.Activate();

        var handle = new WindowInteropHelper(MainWindow).Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatalError(e.Exception);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ShowFatalError(exception);
            return;
        }

        ShowFatalError(new Exception("Unhandled exception in AppDomain."));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ShowFatalError(e.Exception);
    }

    private void ShowFatalError(Exception exception)
    {
        if (_fatalWindowShown)
            return;

        _fatalWindowShown = true;

        var logPath = FatalErrorReport.Write(exception);
        var window = new FatalErrorWindow(exception, logPath);
        window.ShowDialog();

        Shutdown();
    }

    private static void Log(string message) =>
        Trace.WriteLine($"[Shoots.UI] {message}");

    private static string FormatSurfaceList(IReadOnlyList<string> surfaces)
        => surfaces.Count == 0 ? "none" : string.Join(", ", surfaces);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
