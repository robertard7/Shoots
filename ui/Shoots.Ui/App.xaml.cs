using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Shoots.UI.AiHelp;
using Shoots.UI.Diagnostics;
using Shoots.UI.Services;

namespace Shoots.UI;

// This UI reflects and documents state.
// It does not enforce rules, execute logic, validate tools, or control external systems.

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
        UiActionTraceBuffer.EnsureInitialized();

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

        base.OnStartup(e);

        MainWindow = new MainWindow();

        if (TryHandleSmokeMode(e.Args, MainWindow.DataContext as ViewModels.MainWindowViewModel))
        {
            Shutdown();
            return;
        }

        MainWindow.Show();

        UiSurfaceBootstrapper.RegisterAll(MainWindow.DataContext as ViewModels.MainWindowViewModel);
        var surfaceRegistry = AiSurfaceRegistry.Current;
        Log($"AI surface registry: {surfaceRegistry.DescribeRegistrations()}");

        var missingRequired = surfaceRegistry.GetMissingSurfaceIds(UiSurfaceCatalog.RequiredSurfaceIds);
        var missingOptional = surfaceRegistry.GetMissingSurfaceIds(UiSurfaceCatalog.OptionalSurfaceIds);
        var missingOptionalPrefixes = surfaceRegistry.GetMissingSurfacePrefixes(UiSurfaceCatalog.OptionalSurfaceIdPrefixes);
        Log($"AI surfaces missing (required): {FormatSurfaceList(missingRequired)}; missing (optional): {FormatSurfaceList(missingOptional)}; missing (optional prefixes): {FormatSurfaceList(missingOptionalPrefixes)}");

#if DEBUG
        if (missingRequired.Count > 0)
        {
            Log($"DEBUG startup allows missing required AI surfaces: {FormatSurfaceList(missingRequired)}");
        }
#else
        surfaceRegistry.AssertRequiredSurfacesRegistered(UiSurfaceCatalog.RequiredSurfaceIds);
#endif
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

    private static bool TryHandleSmokeMode(string[] args, ViewModels.MainWindowViewModel? viewModel)
    {
        if (args.Length < 2 || !string.Equals(args[0], "--smoke", StringComparison.OrdinalIgnoreCase) || viewModel is null)
        {
            return false;
        }

        var action = args[1];
        var payload = args.Length > 2 ? string.Join(" ", args.Skip(2)) : string.Empty;
        var result = "ok";

        try
        {
            switch (action)
            {
                case "create-project":
                    viewModel.NewProjectCommand.ExecuteAsync().GetAwaiter().GetResult();
                    break;
                case "run-demo":
                    viewModel.NewProjectCommand.ExecuteAsync().GetAwaiter().GetResult();
                    viewModel.RunDemoPlanCommand.ExecuteAsync().GetAwaiter().GetResult();
                    break;
                case "intent":
                    viewModel.ChatInputText = payload;
                    viewModel.SendChatIntentCommand.ExecuteAsync().GetAwaiter().GetResult();
                    break;
                default:
                    result = "unknown-smoke-action";
                    break;
            }
        }
        catch (Exception ex)
        {
            result = $"error:{ex.GetType().Name}";
        }

        var project = viewModel.CurrentProject;
        var invariant = project is null
            ? new Projects.ProjectInvariantResult(false, new[] { "project" }, Array.Empty<string>(), new[] { "no project loaded" })
            : Projects.ProjectInvariants.Verify(project.WorkspacePath);

        var smokeDir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Shoots.UI", "smoke");
        Directory.CreateDirectory(smokeDir);
        var sentinelPath = Path.Combine(smokeDir, "last.json");
        var hasRunPath = project is not null && !string.IsNullOrWhiteSpace(viewModel.LastDemoRunPath);
        var runPath = hasRunPath ? viewModel.LastDemoRunPath : string.Empty;
        var artifactVerification = hasRunPath
            ? new Builder.ArtifactManager().VerifyArtifacts(runPath!)
            : new Builder.ArtifactVerificationResult(false, Array.Empty<string>());
        var sentinel = new
        {
            project_id = project?.ProjectId ?? string.Empty,
            workspace_path = project?.WorkspacePath ?? string.Empty,
            createdUtc = project?.CreatedUtc,
            required_folders_present = invariant.Ok,
            missing = invariant.Missing,
            last_intent = payload,
            outcome = result,
            demo_run_id = hasRunPath ? Path.GetFileName(runPath) : string.Empty,
            run_json_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "run.json")),
            artifact_json_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "artifact.json")),
            environment_json_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "environment.json")),
            manifest_json_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "artifacts", "manifest.json")),
            evidence_bundle_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "evidence_bundle.json")),
            verification_report_exists = hasRunPath && File.Exists(Path.Combine(runPath!, "verification_report.json")),
            log_artifact_exists = hasRunPath && Directory.Exists(Path.Combine(runPath!, "artifacts")) && Directory.GetFiles(Path.Combine(runPath!, "artifacts"), "*.log", SearchOption.AllDirectories).Length > 0,
            artifact_verification_ok = hasRunPath && artifactVerification.Ok,
            artifact_verification_errors = artifactVerification.Errors
        };

        File.WriteAllText(sentinelPath, JsonSerializer.Serialize(sentinel, new JsonSerializerOptions { WriteIndented = true }));
        Trace.WriteLine($"[Shoots.UI] smoke.sentinel={sentinelPath}");
        return true;
    }

    private static string FormatSurfaceList(IReadOnlyList<string> surfaces)
        => surfaces.Count == 0 ? "none" : string.Join(", ", surfaces);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
