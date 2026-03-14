using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core.AI;
using Shoots.UI.Blueprints;
using Shoots.UI.Environment;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Builder;
using Shoots.UI.Services;
using Shoots.UI.Services.Backends;
using Shoots.UI.Settings;
using Shoots.UI.ViewModels;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class MainWindowViewModelBackendStatusTests
{
    [Fact]
    public async Task Refresh_backend_status_sets_disabled_reason_when_ollama_unavailable()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, false, "ui.ollama.unreachable", "Ollama unavailable.", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "Qdrant healthy.", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(false, System.Array.Empty<string>(), "ui.ollama.unreachable", "Ollama unavailable.")));

        await vm.RefreshBackendStatusCommand.ExecuteAsync();

        Assert.Contains("ui.ollama.unreachable", vm.BackendDisabledReason);
        Assert.Equal("ui.ollama.unreachable", vm.ModelCatalogError);
    }

    [Fact]
    public void Provider_unavailable_reason_prefers_backend_status_over_model_catalog_error()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, false, "ui.ollama.connection_refused", "down", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(false, System.Array.Empty<string>(), "ui.ollama.bad_json", "bad json")));

        SetPrivateField(vm, "_selectedProviderMode", "ollama");
        SetPrivateField(vm, "_ollamaStatus", new BackendStatus(BackendKind.Ollama, false, "ui.ollama.connection_refused", "down", System.DateTimeOffset.UtcNow, "http://localhost:11434", null));
        SetPrivateField(vm, "_modelCatalogError", "ui.ollama.bad_json");

        Assert.Contains("ui.ollama.connection_refused", vm.ProviderAvailabilityWarning, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_backend_status_loads_sorted_models()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "Ollama healthy.", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "Qdrant healthy.", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "zeta", "Alpha" }, null, "ok")));

        await vm.RefreshBackendStatusCommand.ExecuteAsync();

        Assert.Equal(new[] { "Alpha", "zeta" }, vm.AvailableModels);
        Assert.Equal("Alpha", vm.SelectedModelId);
        Assert.False(vm.HasModelCatalogError);
    }

    [Fact]
    public async Task Run_intake_plan_is_disabled_with_stable_reason_when_backend_unavailable()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, false, "ui.ollama.unreachable", "Ollama unavailable.", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "Qdrant healthy.", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(false, System.Array.Empty<string>(), "ui.ollama.unreachable", "Ollama unavailable.")));

        await vm.NewProjectCommand.ExecuteAsync();
        vm.IntakeIntent = "run deterministic builder";
        await vm.RefreshBackendStatusCommand.ExecuteAsync();

        Assert.False(vm.RunIntakePlanCommand.CanExecute(null));
        Assert.Contains("ui.ollama.unreachable", vm.RunIntakePlanDisabledReason);
    }


    [Fact]
    public void Apply_environment_has_stable_blocker_reason_without_profile()
    {
        var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            includeProfile: false);

        Assert.Equal("ui.environment.profile.missing: select an environment profile.", vm.ApplyEnvironmentDisabledReason);
        Assert.False(vm.ApplyEnvironmentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_backends_exposes_stable_blocker_reason_while_probe_in_flight()
    {
        var probeService = new BlockingBackendProbeService();
        var vm = BuildViewModel(
            probeService,
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        var refreshTask = vm.RefreshBackendStatusCommand.ExecuteAsync();
        await probeService.WaitForProbeStartAsync();

        Assert.Equal("ui.backends.refresh.in_progress: wait for backend probe completion.", vm.RefreshBackendsDisabledReason);
        Assert.False(vm.RefreshBackendStatusCommand.CanExecute(null));
        Assert.Equal("Waiting on provider", vm.CurrentOperationStage);
        Assert.Equal("active", vm.CurrentOperationStatus);

        probeService.Release();
        await refreshTask;
    }

    [Fact]
    public async Task Copy_artifact_path_commands_route_through_workspace_shell_service()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-copy-test-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        CreateRunArtifacts(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            Assert.True(vm.CopyLastRunFolderPathCommand.CanExecute(null));
            Assert.True(vm.CopyLastVerificationReportPathCommand.CanExecute(null));
            Assert.True(vm.CopyLastOperatorFlowPathCommand.CanExecute(null));
            Assert.True(vm.CopyLastTransportEquivalencePathCommand.CanExecute(null));

            await vm.CopyLastRunFolderPathCommand.ExecuteAsync();
            await vm.CopyLastVerificationReportPathCommand.ExecuteAsync();
            await vm.CopyLastOperatorFlowPathCommand.ExecuteAsync();
            await vm.CopyLastTransportEquivalencePathCommand.ExecuteAsync();

            Assert.Equal(4, shell.CopiedTexts.Count);
            Assert.Equal(runPath, shell.CopiedTexts[0]);
            Assert.Equal(Path.Combine(runPath, "verification_report.json"), shell.CopiedTexts[1]);
            Assert.Equal(Path.Combine(runPath, "operator_flow.json"), shell.CopiedTexts[2]);
            Assert.Equal(Path.Combine(runPath, "transport_equivalence.json"), shell.CopiedTexts[3]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }


    [Fact]
    public async Task Copy_artifact_path_commands_are_disabled_when_targets_missing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-copy-missing-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            Assert.True(vm.CopyLastRunFolderPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastVerificationReportPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastOperatorFlowPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastTransportEquivalencePathCommand.CanExecute(null));

            await vm.CopyLastRunFolderPathCommand.ExecuteAsync();
            await vm.CopyLastVerificationReportPathCommand.ExecuteAsync();
            await vm.CopyLastOperatorFlowPathCommand.ExecuteAsync();
            await vm.CopyLastTransportEquivalencePathCommand.ExecuteAsync();

            Assert.Single(shell.CopiedTexts);
            Assert.Equal(runPath, shell.CopiedTexts[0]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }


    [Fact]
    public async Task Open_artifact_commands_route_exact_expected_paths_through_workspace_shell_service()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-open-test-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);
        var verificationPath = Path.Combine(runPath, "verification_report.json");
        var operatorFlowPath = Path.Combine(runPath, "operator_flow.json");
        var transportPath = Path.Combine(runPath, "transport_equivalence.json");
        File.WriteAllText(verificationPath, "{}\n");
        File.WriteAllText(operatorFlowPath, "{}\n");
        File.WriteAllText(transportPath, "{}\n");

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            Assert.True(vm.OpenLastRunFolderCommand.CanExecute(null));
            Assert.True(vm.OpenLastVerificationReportCommand.CanExecute(null));
            Assert.True(vm.OpenLastOperatorFlowCommand.CanExecute(null));
            Assert.True(vm.OpenLastTransportEquivalenceCommand.CanExecute(null));

            await vm.OpenLastRunFolderCommand.ExecuteAsync();
            await vm.OpenLastVerificationReportCommand.ExecuteAsync();
            await vm.OpenLastOperatorFlowCommand.ExecuteAsync();
            await vm.OpenLastTransportEquivalenceCommand.ExecuteAsync();

            Assert.Equal(4, shell.OpenedPaths.Count);
            Assert.Equal(runPath, shell.OpenedPaths[0]);
            Assert.Equal(verificationPath, shell.OpenedPaths[1]);
            Assert.Equal(operatorFlowPath, shell.OpenedPaths[2]);
            Assert.Equal(transportPath, shell.OpenedPaths[3]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Open_artifact_commands_are_disabled_when_targets_missing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-open-missing-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            Assert.True(vm.OpenLastRunFolderCommand.CanExecute(null));
            Assert.False(vm.OpenLastVerificationReportCommand.CanExecute(null));
            Assert.False(vm.OpenLastOperatorFlowCommand.CanExecute(null));
            Assert.False(vm.OpenLastTransportEquivalenceCommand.CanExecute(null));

            await vm.OpenLastRunFolderCommand.ExecuteAsync();
            await vm.OpenLastVerificationReportCommand.ExecuteAsync();
            await vm.OpenLastOperatorFlowCommand.ExecuteAsync();
            await vm.OpenLastTransportEquivalenceCommand.ExecuteAsync();

            Assert.Single(shell.OpenedPaths);
            Assert.Equal(runPath, shell.OpenedPaths[0]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }


    [Fact]
    public async Task Missing_proof_files_only_route_run_folder_for_open_and_copy_commands()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-open-copy-missing-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            Assert.True(vm.OpenLastRunFolderCommand.CanExecute(null));
            Assert.False(vm.OpenLastVerificationReportCommand.CanExecute(null));
            Assert.False(vm.OpenLastOperatorFlowCommand.CanExecute(null));
            Assert.False(vm.OpenLastTransportEquivalenceCommand.CanExecute(null));

            Assert.True(vm.CopyLastRunFolderPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastVerificationReportPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastOperatorFlowPathCommand.CanExecute(null));
            Assert.False(vm.CopyLastTransportEquivalencePathCommand.CanExecute(null));

            await vm.OpenLastRunFolderCommand.ExecuteAsync();
            await vm.OpenLastVerificationReportCommand.ExecuteAsync();
            await vm.OpenLastOperatorFlowCommand.ExecuteAsync();
            await vm.OpenLastTransportEquivalenceCommand.ExecuteAsync();

            await vm.CopyLastRunFolderPathCommand.ExecuteAsync();
            await vm.CopyLastVerificationReportPathCommand.ExecuteAsync();
            await vm.CopyLastOperatorFlowPathCommand.ExecuteAsync();
            await vm.CopyLastTransportEquivalencePathCommand.ExecuteAsync();

            Assert.Single(shell.OpenedPaths);
            Assert.Single(shell.CopiedTexts);
            Assert.Equal(runPath, shell.OpenedPaths[0]);
            Assert.Equal(runPath, shell.CopiedTexts[0]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }





    [Fact]
    public async Task Copy_commands_do_not_route_open_calls()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-copy-no-open-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        CreateRunArtifacts(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            await vm.CopyLastRunFolderPathCommand.ExecuteAsync();
            await vm.CopyLastVerificationReportPathCommand.ExecuteAsync();
            await vm.CopyLastOperatorFlowPathCommand.ExecuteAsync();
            await vm.CopyLastTransportEquivalencePathCommand.ExecuteAsync();

            Assert.Equal(4, shell.CopiedTexts.Count);
            Assert.Empty(shell.OpenedPaths);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Open_commands_do_not_route_copy_calls()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-open-no-copy-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);
        File.WriteAllText(Path.Combine(runPath, "verification_report.json"), "{}\n");
        File.WriteAllText(Path.Combine(runPath, "operator_flow.json"), "{}\n");
        File.WriteAllText(Path.Combine(runPath, "transport_equivalence.json"), "{}\n");

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            var field = typeof(MainWindowViewModel).GetField("_lastDemoRunPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(vm, runPath);

            await vm.OpenLastRunFolderCommand.ExecuteAsync();
            await vm.OpenLastVerificationReportCommand.ExecuteAsync();
            await vm.OpenLastOperatorFlowCommand.ExecuteAsync();
            await vm.OpenLastTransportEquivalenceCommand.ExecuteAsync();

            Assert.Equal(4, shell.OpenedPaths.Count);
            Assert.Empty(shell.CopiedTexts);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WorkspaceShellService_copy_is_noop_for_empty_or_canceled_requests()
    {
        var shell = new WorkspaceShellService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceled = await Record.ExceptionAsync(() => shell.CopyTextAsync("sample-path", cts.Token));
        var empty = await Record.ExceptionAsync(() => shell.CopyTextAsync(string.Empty));
        var whitespace = await Record.ExceptionAsync(() => shell.CopyTextAsync("   "));

        Assert.Null(canceled);
        Assert.Null(empty);
        Assert.Null(whitespace);
    }

    [Fact]
    public async Task WorkspaceShellService_copy_returns_without_throw_on_non_windows_or_no_app()
    {
        var shell = new WorkspaceShellService();
        var ex = await Record.ExceptionAsync(() => shell.CopyTextAsync("sample-path"));
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_does_not_throw_when_profiles_are_missing()
    {
        var ex = Record.Exception(() => BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            includeProfile: false));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Refresh_backend_status_model_selection_prefers_current_then_preferred_then_first()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new SequenceOllamaClient(
                new OllamaTagsResult(true, new[] { "model-a", "model-b" }, null, "ok"),
                new OllamaTagsResult(true, new[] { "model-c", "model-d" }, null, "ok"),
                new OllamaTagsResult(true, new[] { "model-c", "model-e" }, null, "ok")));

        try
        {
            vm.SelectedModelId = "model-b";
            await vm.RefreshBackendStatusCommand.ExecuteAsync();
            Assert.Equal("model-b", vm.SelectedModelId);

            System.Environment.SetEnvironmentVariable("SHOOTS_PREFERRED_MODEL_ID", "model-d");
            vm.SelectedModelId = "missing";
            await vm.RefreshBackendStatusCommand.ExecuteAsync();
            Assert.Equal("model-d", vm.SelectedModelId);

            System.Environment.SetEnvironmentVariable("SHOOTS_PREFERRED_MODEL_ID", "missing");
            vm.SelectedModelId = "another-missing";
            await vm.RefreshBackendStatusCommand.ExecuteAsync();
            Assert.Equal("model-c", vm.SelectedModelId);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SHOOTS_PREFERRED_MODEL_ID", null);
        }
    }

    [Fact]
    public void Refresh_stage_busy_state_disables_conflicting_actions()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(
            vm,
            "BeginOperationProgress",
            "Refreshing backend",
            "Probing backend health and model catalog.",
            new[] { "Probe Ollama", "Probe Qdrant", "Refresh model catalog" });

        Assert.True(vm.IsOperationActive);
        Assert.Equal("active", vm.CurrentOperationStatus);
        Assert.True(vm.IsOperationBusyIndicatorVisible);
        Assert.False(vm.QuickDemoCommand.CanExecute(null));
        Assert.Contains("refreshing backend", vm.QuickDemoDisabledReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completion_state_holds_then_returns_to_idle()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        vm.OperationCompletionHoldDuration = System.TimeSpan.FromMilliseconds(100);
        await vm.RefreshBackendStatusCommand.ExecuteAsync();

        Assert.Equal("completed", vm.CurrentOperationStatus);
        Assert.True(vm.IsOperationCompletionHoldActive);
        Assert.Equal("busy", vm.BusyState);
        Assert.True(vm.IsOperationBusyIndicatorVisible);

        await Task.Delay(130);
        InvokePrivate(vm, "HandleOperationProgressTimerTick");

        Assert.Equal("idle", vm.CurrentOperationStatus);
        Assert.False(vm.IsOperationVisible);
        Assert.False(vm.IsOperationBusyIndicatorVisible);
        Assert.Equal("Idle", vm.OperationStatusLine);
    }

    [Fact]
    public async Task Quick_demo_exposes_required_timeline_steps()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        await vm.QuickDemoCommand.ExecuteAsync();

        Assert.Equal("completed", vm.CurrentOperationStatus);
        Assert.Equal(
            new[] { "Create project", "Plan run", "Execute tools", "Host run", "Verification", "Completed" },
            vm.OperationProgressSteps.Select(step => step.Name).ToArray());
        Assert.Equal("completed", vm.OperationProgressSteps.Single(step => step.Name == "Completed").State);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, vm.OperationProgressSteps.Select(step => step.StepOrder).ToArray());
        Assert.Equal("Create project", vm.OperationProgressSteps[0].StepName);
        Assert.NotEmpty(vm.OperationNarrationFeed);
    }

    [Fact]
    public async Task Quick_demo_failure_marks_failed_state_then_resets_to_idle()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            planner: new FailingPlanner());

        vm.OperationCompletionHoldDuration = System.TimeSpan.FromMilliseconds(100);
        await vm.QuickDemoCommand.ExecuteAsync();

        Assert.Equal("failed", vm.CurrentOperationStatus);
        Assert.Contains(vm.OperationProgressSteps, step => step.State == "failed");

        await Task.Delay(130);
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.Equal("idle", vm.CurrentOperationStatus);
    }


    [Fact]
    public void Busy_state_and_action_disable_reason_follow_operation_state()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Verification", "Validating outputs.", new[] { "Verification" });

        Assert.Equal("busy", vm.BusyState);
        Assert.Contains("verification", vm.ActionDisableReason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Same(vm.OperationNarrationFeed, vm.NarrationFeed);

        InvokePrivate(vm, "CompleteOperationProgress", true, "Done");
        vm.OperationCompletionHoldDuration = System.TimeSpan.Zero;
        InvokePrivate(vm, "HandleOperationProgressTimerTick");

        Assert.Equal("idle", vm.BusyState);
        Assert.True(string.IsNullOrWhiteSpace(vm.ActionDisableReason));
    }

    [Fact]
    public async Task Refresh_stage_uses_required_working_now_labels()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        await vm.RefreshBackendStatusCommand.ExecuteAsync();

        Assert.Equal("Completed", vm.CurrentOperation);
        Assert.Equal("completed", vm.CurrentOperationStatus);
    }


    [Fact]
    public void Quick_demo_is_disabled_while_verification_is_in_progress()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Verifying run", "Validating run artifacts.", new[] { "Verification" });

        Assert.False(vm.QuickDemoCommand.CanExecute(null));
        Assert.Contains("verification", vm.QuickDemoDisabledReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operation_narration_feed_is_bounded_to_latest_entries()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Planning run", "Preparing", new[] { "Plan run" });
        for (var index = 1; index <= 25; index++)
        {
            InvokePrivate(vm, "SetOperationLatestEvent", $"event-{index}");
        }

        Assert.Equal(20, vm.OperationNarrationFeed.Count);
        Assert.Equal("event-25", vm.OperationNarrationFeed[^1]);
    }

    [Fact]
    public void Narration_feed_survives_completion_hold_and_resets_on_next_operation()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Planning run", "Preparing", new[] { "Plan run" });
        InvokePrivate(vm, "SetOperationLatestEvent", "plan-started");
        InvokePrivate(vm, "CompleteOperationProgress", true, "Done");

        Assert.True(vm.IsOperationCompletionHoldActive);
        Assert.Contains("plan-started", vm.OperationNarrationFeed);

        InvokePrivate(vm, "BeginOperationProgress", "Refreshing backend", "Probing", new[] { "Probe Ollama" });
        Assert.Empty(vm.OperationNarrationFeed);
    }

    [Fact]
    public void Busy_indicator_and_commands_reenable_after_completion_hold_reset()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Verifying run", "Validating run artifacts.", new[] { "Verification" });
        InvokePrivate(vm, "CompleteOperationProgress", true, "Verified");

        Assert.False(vm.QuickDemoCommand.CanExecute(null));
        Assert.True(vm.IsOperationBusyIndicatorVisible);

        vm.OperationCompletionHoldDuration = System.TimeSpan.Zero;
        InvokePrivate(vm, "HandleOperationProgressTimerTick");

        Assert.Equal("idle", vm.BusyState);
        Assert.True(vm.QuickDemoCommand.CanExecute(null));
        Assert.False(vm.IsOperationBusyIndicatorVisible);
    }

    [Fact]
    public void Watchdog_marks_operation_waiting_when_no_progress_is_reported()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Waiting on provider", "Checking endpoint.", new[] { "Probe Ollama" });
        var staleAt = System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25));
        SetPrivateField(vm, "_operationLastProgressUtc", staleAt);

        InvokePrivate(vm, "HandleOperationProgressTimerTick");

        Assert.True(vm.IsOperationWaiting);
        Assert.Contains("No recent progress", vm.OperationWaitHint, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Collapsed_timeline_shows_active_and_recent_steps_in_order()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Planning run", "Preparing", new[] { "A", "B", "C", "D" });
        InvokePrivate(vm, "SetOperationStepState", "A", "completed", "ok");
        InvokePrivate(vm, "SetOperationStepState", "B", "completed", "ok");
        InvokePrivate(vm, "SetOperationStepState", "C", "active", "running");
        vm.ShowFullTimeline = false;

        Assert.Equal(new[] { "A", "B", "C" }, vm.VisibleOperationProgressSteps.Select(step => step.StepName).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, vm.VisibleOperationProgressSteps.Select(step => step.StepOrder).ToArray());
        Assert.Equal("Show full timeline", vm.TimelineToggleLabel);

        vm.ShowFullTimeline = true;
        Assert.Equal("Show active + recent", vm.TimelineToggleLabel);
        Assert.Equal(new[] { "A", "B", "C", "D" }, vm.VisibleOperationProgressSteps.Select(step => step.StepName).ToArray());
    }

    [Fact]
    public void Collapsed_timeline_keeps_failed_step_visible()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Planning run", "Preparing", new[] { "A", "B", "C", "D" });
        InvokePrivate(vm, "SetOperationStepState", "A", "completed", "ok");
        InvokePrivate(vm, "SetOperationStepState", "B", "failed", "boom");
        vm.ShowFullTimeline = false;

        Assert.Contains(vm.VisibleOperationProgressSteps, step => step.StepName == "B" && step.StepState == "failed");
    }

    [Fact]
    public void Snapshot_consistency_prevents_idle_completion_hold_conflict()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        Assert.Equal("idle", vm.CurrentOperationStatus);
        Assert.False(vm.CompletionHold);

        InvokePrivate(vm, "BeginOperationProgress", "Verifying run", "Validating.", new[] { "Verification" });
        Assert.Equal("busy", vm.BusyState);
        Assert.Equal("active", vm.CurrentOperationStatus);
    }

    [Fact]
    public void Waiting_hint_clears_on_narration_stage_and_step_updates()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Waiting on provider", "Checking endpoint.", new[] { "Probe Ollama" });
        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.True(vm.IsOperationWaiting);

        InvokePrivate(vm, "SetOperationLatestEvent", "new narration");
        Assert.False(vm.IsOperationWaiting);

        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.True(vm.IsOperationWaiting);

        InvokePrivate(vm, "SetOperationStatus", "Planning run", "stage update");
        Assert.False(vm.IsOperationWaiting);

        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.True(vm.IsOperationWaiting);

        InvokePrivate(vm, "SetOperationStepState", "Probe Ollama", "active", "step update");
        Assert.False(vm.IsOperationWaiting);
    }

    [Fact]
    public void Waiting_hint_uses_provider_specific_text_for_provider_stage()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "BeginOperationProgress", "Waiting on provider", "Checking Ollama endpoint.", new[] { "Probe Ollama" });
        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");

        Assert.Contains("provider response", vm.OperationWaitHint, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_selected_run_command_loads_saved_run_artifacts_read_only()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-replay-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);

        try
        {
            var metadata = new PersistedRunMetadata(
                "run-001",
                runPath,
                "local",
                "none",
                RunStates.Completed,
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new RunStageRecord("step-01", "completed", "ok", System.DateTimeOffset.UtcNow, System.DateTimeOffset.UtcNow)
                },
                new[]
                {
                    new ProviderAttemptRecord(1, 1, "ready", null, "Provider ready.", System.DateTimeOffset.UtcNow, System.DateTimeOffset.UtcNow)
                },
                null,
                new Dictionary<string, string>
                {
                    ["run.json"] = Path.Combine(runPath, "run.json")
                });
            var run = new RunModel(
                "run-001",
                "project-001",
                "plan-001",
                "plan-hash",
                "catalog-hash",
                "workspace-hash",
                System.DateTimeOffset.UtcNow,
                RunStates.Completed,
                new[]
                {
                    new RunStep("step-01", "tools.sample", RunStates.Completed, null, null)
                },
                ExecutionContract.Version,
                "planner",
                "bridge",
                "local",
                "none");
            File.WriteAllText(Path.Combine(runPath, "run.json"), System.Text.Json.JsonSerializer.Serialize(run, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(runPath, RunReplayService.MetadataFileName), System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(runPath, RunReplayService.TimelineFileName), System.Text.Json.JsonSerializer.Serialize(metadata.StageFlow, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

            var row = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, RunStates.Completed, "local", "none", "Verified");
            vm.SelectedRunHistory = row;

            Assert.True(vm.ReplaySelectedRunCommand.CanExecute(null));
            await vm.ReplaySelectedRunCommand.ExecuteAsync();

            Assert.True(vm.IsReplayMode);
            Assert.Equal(runPath, vm.ReplaySourcePath);
            Assert.Contains("matches saved run metadata", vm.ReplaySummary, System.StringComparison.Ordinal);
            Assert.Contains("original=", vm.ReplayTimingSummary, System.StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runPath, RunReplayService.ReplayDiffFileName)));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Refresh_backend_status_persists_provider_diagnostics_history()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-provider-diag-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, false, "ui.ollama.connection_refused", "down", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(false, System.Array.Empty<string>(), "ui.ollama.connection_refused", "down")));

            SetPrivateField(vm, "_activeWorkspace", new ProjectWorkspace("diag", tempRoot, System.DateTimeOffset.UtcNow, ProjectId: "diag-project"));
            await vm.RefreshBackendStatusCommand.ExecuteAsync();

            var diagnosticsPath = Path.Combine(tempRoot, "provider_diagnostics.json");
            Assert.True(File.Exists(diagnosticsPath));
            var entries = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<MainWindowViewModel.ProviderDiagnosticEventRow>>(File.ReadAllText(diagnosticsPath));
            Assert.NotNull(entries);
            Assert.NotEmpty(entries!);
            Assert.Contains(entries!, entry => entry.Classification == "connection_refused");
            Assert.True(vm.HasProviderDiagnostics);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Waiting_hint_does_not_trigger_when_idle_or_completion_hold_only()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.False(vm.IsOperationWaiting);

        InvokePrivate(vm, "BeginOperationProgress", "Verifying run", "validating", new[] { "Verification" });
        InvokePrivate(vm, "CompleteOperationProgress", true, "done");
        SetPrivateField(vm, "_operationLastProgressUtc", System.DateTimeOffset.UtcNow.Subtract(System.TimeSpan.FromSeconds(25)));
        InvokePrivate(vm, "HandleOperationProgressTimerTick");
        Assert.False(vm.IsOperationWaiting);
    }

    [Fact]
    public void Failure_diagnostics_handle_empty_and_malformed_reason_text()
    {
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

        InvokePrivate(vm, "RecordFailure", "Run Demo", "", vm.UiLogPath, "retry");
        Assert.Equal("Unknown", vm.LastFailureExceptionType);
        Assert.Equal(string.Empty, vm.LastFailureFirstStackFrame);
        Assert.Equal(string.Empty, vm.LastFailureMessage);

        InvokePrivate(vm, "RecordFailure", "Run Demo", "   \t  ", vm.UiLogPath, "retry");
        Assert.Equal("Unknown", vm.LastFailureExceptionType);
        Assert.Equal(string.Empty, vm.LastFailureFirstStackFrame);
        Assert.Equal(string.Empty, vm.LastFailureMessage);

        InvokePrivate(vm, "RecordFailure", "Run Demo", "something bad happened", vm.UiLogPath, "retry");
        Assert.Equal("Unknown", vm.LastFailureExceptionType);
        Assert.Equal(string.Empty, vm.LastFailureFirstStackFrame);
        Assert.Equal("something bad happened", vm.LastFailureMessage);

        var multilinePlain = "plain headline\nadditional detail line";
        InvokePrivate(vm, "RecordFailure", "Run Demo", multilinePlain, vm.UiLogPath, "retry");
        Assert.Equal("Unknown", vm.LastFailureExceptionType);
        Assert.Equal(string.Empty, vm.LastFailureFirstStackFrame);
        Assert.Equal("plain headline", vm.LastFailureMessage);

        var multiline = "InvalidOperationException: boom\nat Demo.Run() in Demo.cs:line 42\nat Main()";
        InvokePrivate(vm, "RecordFailure", "Run Demo", multiline, vm.UiLogPath, "retry");
        Assert.Equal("InvalidOperationException", vm.LastFailureExceptionType);
        Assert.StartsWith("at Demo.Run()", vm.LastFailureFirstStackFrame, System.StringComparison.Ordinal);
        Assert.Equal("boom", vm.LastFailureMessage);

        var extraColon = "InvalidOperationException: could not parse: missing token";
        InvokePrivate(vm, "RecordFailure", "Run Demo", extraColon, vm.UiLogPath, "retry");
        Assert.Equal("could not parse: missing token", vm.LastFailureMessage);
        Assert.EndsWith("fatal-error.log", vm.FatalLogPath, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_last_run_folder_command_handles_missing_deleted_paths_safely()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-open-last-run-edge-{System.Guid.NewGuid():N}");
        var runPath = Path.Combine(tempRoot, "runs", "run-001");
        Directory.CreateDirectory(runPath);

        try
        {
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell);

            SetPrivateField(vm, "_lastDemoRunPath", runPath);
            Assert.True(vm.OpenLastRunFolderCommand.CanExecute(null));

            Directory.Delete(runPath, recursive: true);
            Assert.False(vm.OpenLastRunFolderCommand.CanExecute(null));

            await vm.OpenLastRunFolderCommand.ExecuteAsync();
            Assert.Empty(shell.OpenedPaths);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Async_relay_command_can_invalidate_from_background_thread()
    {
        var command = new AsyncRelayCommand(() => Task.CompletedTask);
        var ex = await Record.ExceptionAsync(() => Task.Run(command.RaiseCanExecuteChanged));
        Assert.Null(ex);
    }

    [Fact]
    public void App_smoke_mode_sentinel_diagnostics_fields_are_declared()
    {
        var root = FindRepoRoot();
        var appSourcePath = Path.Combine(root, "ui", "Shoots.Ui", "App.xaml.cs");
        var appSource = File.ReadAllText(appSourcePath);

        Assert.Contains("run_demo_disabled_reason", appSource, System.StringComparison.Ordinal);
        Assert.Contains("last_failure_phase", appSource, System.StringComparison.Ordinal);
        Assert.Contains("last_failure_reason", appSource, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_commands_are_disabled_while_validation_loop_is_running()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runner = new BlockingValidationRunnerService(repoRoot);
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            var task = vm.RunFullValidationLoopCommand.ExecuteAsync();
            await runner.WaitForStartAsync();

            Assert.False(vm.BuildUiProjectCommand.CanExecute(null));
            Assert.False(vm.RunUiTestsCommand.CanExecute(null));
            Assert.Contains("using the workspace", vm.ValidationDisabledReason, System.StringComparison.Ordinal);

            runner.Release();
            await task;
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_result_surface_updates_after_success()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-001");
        Directory.CreateDirectory(outputFolder);

        var runner = new DeterministicValidationRunnerService(
            repoRoot,
            new ValidationRunResult(
                "run-001",
                "Run full validation loop",
                outputFolder,
                true,
                "Validation passed (2 stages).",
                null,
                null,
                System.DateTimeOffset.UtcNow.AddMinutes(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 50),
                    new ValidationStageResult("ui_tests", "Running UI tests", "passed", "Tests passed.", Path.Combine(outputFolder, "02-ui-tests.log"), 0, 75)
                }));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.RunFullValidationLoopCommand.ExecuteAsync();

            Assert.Equal("Validation passed (2 stages).", vm.ValidationSummary);
            Assert.True(vm.HasValidationOutputFolder);
            Assert.Equal(2, vm.ValidationStageResults.Count);
            Assert.Equal("passed", vm.ValidationStageResults[0].Status);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_result_surface_updates_after_failure()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-002");
        Directory.CreateDirectory(outputFolder);
        var failureLog = Path.Combine(outputFolder, "02-ui-tests.log");
        File.WriteAllText(failureLog, "failed");

        var runner = new DeterministicValidationRunnerService(
            repoRoot,
            new ValidationRunResult(
                "run-002",
                "Run full validation loop",
                outputFolder,
                false,
                "Validation failed: Tests failed.",
                "Tests failed.",
                failureLog,
                System.DateTimeOffset.UtcNow.AddMinutes(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 50),
                    new ValidationStageResult("ui_tests", "Running UI tests", "failed", "Tests failed.", failureLog, 1, 75)
                }));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.RunFullValidationLoopCommand.ExecuteAsync();

            Assert.Equal("Validation failed: Tests failed.", vm.ValidationSummary);
            Assert.True(vm.HasValidationFirstFailure);
            Assert.Equal("Tests failed.", vm.ValidationFirstFailureText);
            Assert.Equal(failureLog, vm.ValidationFirstFailureLogPath);
            Assert.Equal("failed", vm.ValidationStageResults[1].Status);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_settings_changes_are_persisted()
    {
        var settingsStore = new InMemoryValidationSettingsStore();
        var repoRoot = CreateValidationRepoRoot();
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: settingsStore);

        try
        {
            vm.ContinueValidationOnFailure = true;
            vm.IncludeValidateBuildForFullLoop = true;
            vm.AutoOpenValidationLogsOnFailure = true;
            vm.EnableIsolatedValidationWorkspaceMode = true;
            vm.ValidateGeneratedOutputAfterRun = true;
            vm.EnableValidationStabilityRetry = true;
            vm.SelectedValidationKeepLastRuns = 10;
            vm.SelectedValidationHistoryRetentionCount = 50;
            vm.SelectedValidationRegressionComparisonWindow = 10;
            vm.CountRetryPassesAsStableInTrendSummaries = true;
            vm.SelectedValidationBaselineHistoryRetentionCount = 10;
            vm.CountPassedOnRetryAsReleaseReady = true;
            vm.FlakySuspectedBlocksReleaseReadiness = false;
            vm.EnableSemanticReuseSuggestions = true;
            vm.SelectedSemanticReuseMaxCases = 8;
            vm.SelectedSemanticReuseRetentionCount = 500;
            vm.IndexProviderDiagnosticsEpisodes = false;
            vm.OnlyShowPassingOrImprovedReuseCases = true;
            vm.IncludePromotedRepairSuggestions = false;
            vm.IncludeProviderEpisodeSuggestions = false;
            vm.EnablePlaybookSuggestions = false;
            vm.SelectedPlaybookMinimumEvidenceCount = 4;
            vm.ShowTentativePlaybooks = false;
            vm.SelectedSemanticReuseMaxPlaybooks = 5;

            Assert.NotNull(settingsStore.LastSaved);
            Assert.True(settingsStore.LastSaved!.ContinueOnFailure);
            Assert.True(settingsStore.LastSaved.IncludeValidateBuild);
            Assert.True(settingsStore.LastSaved.AutoOpenLogsOnFailure);
            Assert.True(settingsStore.LastSaved.EnableIsolatedValidationWorkspaceMode);
            Assert.True(settingsStore.LastSaved.ValidateGeneratedOutputAfterRun);
            Assert.True(settingsStore.LastSaved.EnableStabilityRetry);
            Assert.Equal(10, settingsStore.LastSaved.KeepLastRuns);
            Assert.Equal(50, settingsStore.LastSaved.HistoryRetentionCount);
            Assert.Equal(10, settingsStore.LastSaved.RegressionComparisonWindow);
            Assert.True(settingsStore.LastSaved.CountRetryPassesAsStableInSummaries);
            Assert.Equal(10, settingsStore.LastSaved.BaselineHistoryRetentionCount);
            Assert.True(settingsStore.LastSaved.CountPassedOnRetryAsReleaseReady);
            Assert.False(settingsStore.LastSaved.FlakySuspectedBlocksReleaseReadiness);
            Assert.True(settingsStore.LastSaved.EnableSemanticReuseSuggestions);
            Assert.Equal(8, settingsStore.LastSaved.MaxSemanticReuseCases);
            Assert.Equal(500, settingsStore.LastSaved.SemanticReuseRetentionCount);
            Assert.False(settingsStore.LastSaved.IndexProviderDiagnosticsEpisodes);
            Assert.True(settingsStore.LastSaved.OnlyShowPassingOrImprovedReuseCases);
            Assert.False(settingsStore.LastSaved.IncludePromotedRepairSuggestions);
            Assert.False(settingsStore.LastSaved.IncludeProviderEpisodeSuggestions);
            Assert.False(settingsStore.LastSaved.EnablePlaybookSuggestions);
            Assert.Equal(4, settingsStore.LastSaved.MinimumPlaybookEvidenceCount);
            Assert.False(settingsStore.LastSaved.ShowTentativePlaybooks);
            Assert.Equal(5, settingsStore.LastSaved.MaxPlaybooksPerContext);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Smoke_and_integrity_validation_conflicts_have_specific_disable_reasons()
    {
        var repoRoot = CreateValidationRepoRoot();
        var smokeRunner = new BlockingValidationRunnerService(
            repoRoot,
            ValidationAction.RunSmokeValidation,
            "Run smoke validation",
            "smoke_validation",
            "Running smoke validation");
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: smokeRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            var smokeTask = vm.RunSmokeValidationCommand.ExecuteAsync();
            await smokeRunner.WaitForStartAsync();

            Assert.False(vm.RunIntegrityValidationCommand.CanExecute(null));
            Assert.Equal("Integrity validation is blocked while smoke validation is using the workspace.", vm.RunIntegrityValidationDisabledReason);

            smokeRunner.Release();
            await smokeTask;

            var integrityRunner = new BlockingValidationRunnerService(
                repoRoot,
                ValidationAction.RunIntegrityValidation,
                "Run integrity validation",
                "integrity_validation",
                "Running integrity validation");
            vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationRunnerService: integrityRunner,
                validationSettingsStore: new InMemoryValidationSettingsStore());

            var integrityTask = vm.RunIntegrityValidationCommand.ExecuteAsync();
            await integrityRunner.WaitForStartAsync();

            Assert.False(vm.RunSmokeValidationCommand.CanExecute(null));
            Assert.Equal("Smoke validation must finish before integrity can clean restore artifacts.", vm.RunSmokeValidationDisabledReason);

            integrityRunner.Release();
            await integrityTask;
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_scheduling_surface_reflects_isolated_workspace_policy()
    {
        var repoRoot = CreateValidationRepoRoot();
        var settingsStore = new InMemoryValidationSettingsStore
        {
            Current = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, false, 5, 200, true, false, true, true, true, 2, true, 3, true)
        };
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: settingsStore);

        try
        {
            var buildRow = Assert.Single(vm.ValidationActionPolicies, row => string.Equals(row.ActionLabel, "Build UI project", System.StringComparison.Ordinal));
            var integrityRow = Assert.Single(vm.ValidationActionPolicies, row => string.Equals(row.ActionLabel, "Run integrity validation", System.StringComparison.Ordinal));

            Assert.Equal("Isolated workspace mode", buildRow.RunModeLabel);
            Assert.Contains("parallel-safe", buildRow.ClassificationSummary, System.StringComparison.Ordinal);
            Assert.Contains("workspace-cleaning", integrityRow.ClassificationSummary, System.StringComparison.Ordinal);
            Assert.Contains("repo root", integrityRow.IsolationSummary, System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_result_surface_shows_orchestration_mode_and_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-isolated");
        Directory.CreateDirectory(outputFolder);
        var orchestrationPath = Path.Combine(outputFolder, "validation_orchestration.json");
        var policyPath = Path.Combine(repoRoot, ".codex", "validation-ui", "validation_orchestration_policy.md");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllText(orchestrationPath, "{}");
        File.WriteAllText(policyPath, "# policy");
        var isolatedWorkspace = Path.Combine(outputFolder, "isolated-workspace");
        Directory.CreateDirectory(isolatedWorkspace);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(
                repoRoot,
                new ValidationRunResult(
                    "run-isolated",
                    "Build UI project",
                    outputFolder,
                    true,
                    "Validation passed (1 stage).",
                    null,
                    null,
                    System.DateTimeOffset.UtcNow.AddMinutes(-1),
                    System.DateTimeOffset.UtcNow,
                    new[]
                    {
                        new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 25)
                    },
                    "passed",
                    "Passed cleanly",
                    null,
                    null,
                    Path.Combine(outputFolder, "validation_stability.json"),
                    "isolated_workspace_mode",
                    orchestrationPath,
                    isolatedWorkspace)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.BuildUiProjectCommand.ExecuteAsync();

            Assert.Equal("Isolated workspace mode", vm.ValidationRunModeBadge);
            Assert.True(vm.HasValidationOrchestrationArtifactPath);
            Assert.Equal(orchestrationPath, vm.ValidationOrchestrationArtifactPath);
            Assert.True(vm.HasValidationOrchestrationNotePath);
            Assert.Equal(policyPath, vm.ValidationOrchestrationNotePath);
            Assert.True(vm.HasValidationIsolatedWorkspacePath);
            Assert.Equal(isolatedWorkspace, vm.ValidationIsolatedWorkspacePath);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_handoff_surface_loads_latest_bundle_and_comparison()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);
        var summaryPath = ValidationRunnerService.HandoffSummaryPathForRun(latestResult.OutputFolder);
        var bundlePath = ValidationRunnerService.HandoffBundlePathForRun(latestResult.OutputFolder);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.True(vm.HasValidationHandoffSummaryPath);
            Assert.Equal(summaryPath, vm.ValidationHandoffSummaryPath);
            Assert.True(vm.HasValidationHandoffBundlePath);
            Assert.Equal(bundlePath, vm.ValidationHandoffBundlePath);
            Assert.True(vm.HasValidationHandoffSummary);
            Assert.Contains("failed / Failed / not ready.", vm.ValidationHandoffSummaryText, System.StringComparison.Ordinal);
            Assert.Contains("First failure: Running UI tests: Tests failed.", vm.ValidationHandoffSummaryText, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationHandoffComparisonSummary);
            Assert.Contains("Result passed -> failed", vm.ValidationHandoffComparisonSummary, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationFollowupIntakePath);
            Assert.True(vm.HasValidationFollowupPromptPath);
            Assert.Equal("Fix tests", vm.ValidationFollowupBadge);
            Assert.Contains("fix tests.", vm.ValidationFollowupSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Isolate the first failing test", vm.ValidationFollowupNextStepText, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_handoff_helpers_open_and_copy_latest_bundle_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);
        var shell = new RecordingWorkspaceShellService();

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            workspaceShell: shell,
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.OpenValidationHandoffSummaryCommand.ExecuteAsync();
            await vm.OpenValidationHandoffBundleFolderCommand.ExecuteAsync();
            await vm.CopyValidationHandoffSummaryCommand.ExecuteAsync();
            await vm.CopyValidationHandoffArtifactPathsCommand.ExecuteAsync();
            await vm.OpenValidationFollowupIntakeCommand.ExecuteAsync();
            await vm.OpenValidationFollowupPromptCommand.ExecuteAsync();
            await vm.CopyValidationFollowupSummaryCommand.ExecuteAsync();
            await vm.CopyValidationFollowupPromptCommand.ExecuteAsync();

            Assert.Contains(ValidationRunnerService.HandoffSummaryPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(latestResult.OutputFolder, shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.FollowupIntakePathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.FollowupPromptPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Equal(4, shell.CopiedTexts.Count);
            Assert.Contains("# Validation Handoff Summary", shell.CopiedTexts[0], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.HandoffBundlePathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(Path.Combine(latestResult.OutputFolder, "validation_result.json"), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.FollowupPlanPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.FollowupExecutionPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.FollowupExecutionOutcomePathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.FollowupEscalationPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.FollowupResolutionReviewPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.ResolutionHandoffPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.ResolutionPromotionReviewPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.ReleaseDecisionSummaryPathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains(ValidationRunnerService.RepairPrepBundlePathForRun(latestResult.OutputFolder), shell.CopiedTexts[1], System.StringComparison.Ordinal);
            Assert.Contains("fix tests.", shell.CopiedTexts[2], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Follow-up category: fix_tests", shell.CopiedTexts[3], System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_followup_plan_surface_loads_latest_plan_and_repair_prep_bundle()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.True(vm.HasValidationFollowupPlanPath);
            Assert.Equal(ValidationRunnerService.FollowupPlanPathForRun(latestResult.OutputFolder), vm.ValidationFollowupPlanPath);
            Assert.True(vm.HasValidationRepairPrepBundlePath);
            Assert.Equal(ValidationRunnerService.RepairPrepBundlePathForRun(latestResult.OutputFolder), vm.ValidationRepairPrepBundlePath);
            Assert.True(vm.HasValidationFollowupPlanSummary);
            Assert.Contains("fix tests plan.", vm.ValidationFollowupPlanSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Inspect first failing test", vm.ValidationFollowupPlanSummaryText, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationFollowupRerunRecommendation);
            Assert.Contains("Rerun the first failing test or test project", vm.ValidationFollowupRerunRecommendationText, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationRepairPrepSummary);
            Assert.Contains("repair prep", vm.ValidationRepairPrepSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasValidationFollowupPlanFreshness);
            Assert.Equal("Current plan for the latest validation run.", vm.ValidationFollowupPlanFreshnessText);
            Assert.Equal("Do not promote", vm.ValidationResolutionPromotionBadge);
            Assert.True(vm.HasValidationResolutionPromotionSummary);
            Assert.True(vm.HasValidationResolutionPromotionReviewPath);
            Assert.Equal("Resolution not stable enough", vm.ValidationReleaseDecisionBadge);
            Assert.True(vm.HasValidationReleaseDecisionSummary);
            Assert.True(vm.HasValidationReleaseDecisionNotesSummary);
            Assert.True(vm.HasValidationReleaseDecisionSummaryPath);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_followup_plan_helpers_open_and_copy_latest_plan_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);
        var shell = new RecordingWorkspaceShellService();

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            workspaceShell: shell,
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.OpenValidationFollowupPlanCommand.ExecuteAsync();
            await vm.OpenValidationRepairPrepBundleCommand.ExecuteAsync();
            await vm.CopyValidationFollowupPlanSummaryCommand.ExecuteAsync();
            await vm.CopyValidationRepairPrepSummaryCommand.ExecuteAsync();
            await vm.CopyValidationFollowupRerunRecommendationCommand.ExecuteAsync();

            Assert.Contains(ValidationRunnerService.FollowupPlanPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.RepairPrepBundlePathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Equal(3, shell.CopiedTexts.Count);
            Assert.Contains("fix tests plan.", shell.CopiedTexts[0], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("repair prep", shell.CopiedTexts[1], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rerun the first failing test or test project", shell.CopiedTexts[2], System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_followup_plan_surface_exposes_step_actions_and_tracking()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);
        var shell = new RecordingWorkspaceShellService();

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            workspaceShell: shell,
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.True(vm.HasValidationFollowupPlanSteps);
            Assert.Equal(
                new[]
                {
                    "inspect_test_failure",
                    "inspect_artifact",
                    "rerun_single_test_or_project",
                    "prepare_repair_bundle",
                    "rerun_single_stage"
                },
                vm.ValidationFollowupPlanSteps.Select(step => step.StepType).ToArray());
            Assert.Equal("View ready", vm.ValidationFollowupPlanSteps[0].ExecutionAvailability);
            Assert.Equal("Rerun ready", vm.ValidationFollowupPlanSteps[2].ExecutionAvailability);

            await vm.OpenValidationFollowupFirstEvidenceCommand.ExecuteAsync();
            await vm.CopyValidationFollowupRerunCommandSummaryCommand.ExecuteAsync();

            Assert.Contains(latestResult.FirstFailureLogPath!, shell.OpenedPaths);
            Assert.Contains("dotnet test .\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj -c Debug -v minimal", shell.CopiedTexts);

            var openedStep = Assert.Single(vm.ValidationFollowupPlanSteps, step => step.StepType == "inspect_test_failure");
            Assert.Equal("Opened", openedStep.CompletionBadge);

            var copiedStep = Assert.Single(vm.ValidationFollowupPlanSteps, step => step.StepType == "rerun_single_test_or_project");
            Assert.Equal("Copied", copiedStep.CompletionBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guided_validation_rerun_records_linkage_and_blocks_superseded_plan_actions()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.True(vm.RunValidationFollowupRecommendedRerunCommand.CanExecute(null));

            await vm.RunValidationFollowupRecommendedRerunCommand.ExecuteAsync();

            Assert.True(vm.HasValidationFollowupRerunOutcome);
            Assert.Contains("stayed the same", vm.ValidationFollowupRerunOutcomeSummary, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Unchanged", vm.ValidationFollowupOutcomeBadge);
            Assert.True(vm.HasValidationFollowupOutcomeSummary);
            Assert.Contains("stayed unchanged", vm.ValidationFollowupOutcomeSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasValidationFollowupOutcomeNextStateText);
            Assert.Contains("Prepare a repair bundle", vm.ValidationFollowupOutcomeNextStateText, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationFollowupOutcomeFreshnessText);
            Assert.Contains("Superseded by validation run", vm.ValidationFollowupOutcomeFreshnessText, System.StringComparison.Ordinal);
            Assert.Equal("Watch recurring issue", vm.ValidationFollowupEscalationBadge);
            Assert.True(vm.HasValidationFollowupEscalationSummary);
            Assert.Equal("Superseded", vm.ValidationFollowupResolutionBadge);
            Assert.True(vm.HasValidationFollowupResolutionSummary);
            Assert.Contains("superseded by newer validation evidence", vm.ValidationFollowupResolutionSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasValidationFollowupResolutionClosureText);
            Assert.Contains("still open", vm.ValidationFollowupResolutionClosureText, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasValidationFollowupResolutionFreshnessText);
            Assert.Contains("Superseded by later follow-up", vm.ValidationFollowupResolutionFreshnessText, System.StringComparison.Ordinal);
            Assert.Equal("No handoff", vm.ValidationResolutionHandoffBadge);
            Assert.True(vm.HasValidationResolutionHandoffSummary);
            Assert.Equal("Do not promote", vm.ValidationResolutionPromotionBadge);
            Assert.True(vm.HasValidationResolutionPromotionSummary);
            Assert.Equal("Resolution not stable enough", vm.ValidationReleaseDecisionBadge);
            Assert.True(vm.HasValidationReleaseDecisionSummary);
            Assert.Contains("Superseded by newer validation run", vm.ValidationFollowupPlanFreshnessText, System.StringComparison.Ordinal);
            Assert.False(vm.RunValidationFollowupRecommendedRerunCommand.CanExecute(null));
            Assert.Contains("no longer the latest validation plan", vm.ValidationFollowupRecommendedRerunBlockedReason, System.StringComparison.Ordinal);

            var rerunStep = Assert.Single(vm.ValidationFollowupPlanSteps, step => step.StepType == "rerun_single_test_or_project");
            Assert.Equal("Completed by validation", rerunStep.CompletionBadge);
            Assert.Equal("Blocked", rerunStep.ExecutionAvailability);

            var execution = ValidationRunnerService.LoadFollowupExecutionStateForRun(latestResult.OutputFolder);
            Assert.NotNull(execution);
            Assert.NotNull(execution!.LatestRerun);
            Assert.Equal(latestResult.RunId, execution.LatestRerun!.SourceValidationRunId);
            Assert.NotEqual(latestResult.RunId, execution.LatestRerun.RerunValidationRunId);
            Assert.Equal("unchanged", execution.LatestRerun.OutcomeClassification);
            Assert.True(File.Exists(ValidationRunnerService.FollowupExecutionPathForRun(latestResult.OutputFolder)));
            Assert.True(vm.HasValidationFollowupExecutionOutcomePath);
            Assert.True(vm.HasValidationFollowupEscalationPath);
            Assert.True(vm.HasValidationFollowupResolutionReviewPath);
            Assert.True(vm.HasValidationResolutionHandoffPath);
            Assert.True(vm.HasValidationResolutionPromotionReviewPath);
            Assert.True(vm.HasValidationReleaseDecisionSummaryPath);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guided_followup_outcome_helpers_open_and_copy_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();
        var (validationRunner, latestResult) = await SeedValidationHandoffArtifactsAsync(repoRoot);
        var shell = new RecordingWorkspaceShellService();

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            workspaceShell: shell,
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.RunValidationFollowupRecommendedRerunCommand.ExecuteAsync();
            await vm.OpenValidationFollowupExecutionOutcomeCommand.ExecuteAsync();
            await vm.OpenValidationFollowupEscalationCommand.ExecuteAsync();
            await vm.OpenValidationFollowupResolutionReviewCommand.ExecuteAsync();
            await vm.OpenValidationResolutionHandoffCommand.ExecuteAsync();
            await vm.OpenValidationResolutionPromotionReviewCommand.ExecuteAsync();
            await vm.OpenValidationReleaseDecisionSummaryCommand.ExecuteAsync();
            await vm.OpenValidationFollowupRerunArtifactsCommand.ExecuteAsync();
            await vm.CopyValidationFollowupOutcomeNextStepCommand.ExecuteAsync();
            await vm.CopyValidationFollowupEscalationSummaryCommand.ExecuteAsync();
            await vm.CopyValidationFollowupClosureSummaryCommand.ExecuteAsync();
            await vm.CopyValidationResolutionHandoffSummaryCommand.ExecuteAsync();
            await vm.CopyValidationResolutionPromotionSummaryCommand.ExecuteAsync();
            await vm.CopyValidationReleaseDecisionSummaryCommand.ExecuteAsync();

            var execution = ValidationRunnerService.LoadFollowupExecutionStateForRun(latestResult.OutputFolder);
            Assert.NotNull(execution);
            Assert.NotNull(execution!.LatestRerun);
            Assert.Contains(ValidationRunnerService.FollowupExecutionOutcomePathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.FollowupEscalationPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.FollowupResolutionReviewPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.ResolutionHandoffPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.ResolutionPromotionReviewPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(ValidationRunnerService.ReleaseDecisionSummaryPathForRun(latestResult.OutputFolder), shell.OpenedPaths);
            Assert.Contains(execution.LatestRerun!.RerunValidationOutputFolder, shell.OpenedPaths);
            Assert.Contains("Prepare a repair bundle", shell.CopiedTexts[0], System.StringComparison.Ordinal);
            Assert.Contains("Repeated unresolved outcomes", shell.CopiedTexts[1], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Original issue:", shell.CopiedTexts[2], System.StringComparison.Ordinal);
            Assert.Contains("still open", shell.CopiedTexts[2], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No baseline or readiness handoff", shell.CopiedTexts[3], System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do not promote.", shell.CopiedTexts[4], System.StringComparison.Ordinal);
            Assert.Contains("Decision state:", shell.CopiedTexts[5], System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Similar_cases_surface_updates_from_semantic_reuse_service()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-failed");
        Directory.CreateDirectory(outputFolder);
        var failedResult = new ValidationRunResult(
            "run-failed",
            "Run full validation loop",
            outputFolder,
            false,
            "Validation failed: Tests failed.",
            "Tests failed.",
            Path.Combine(outputFolder, "02-ui-tests.log"),
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            System.DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("ui_tests", "Running UI tests", "failed", "Tests failed.", Path.Combine(outputFolder, "02-ui-tests.log"), 1, 40)
            },
            "failed",
            "Failed",
            new ValidationFirstFailure(
                "ui_tests",
                "Running UI tests",
                "Shoots.Runtime.Tests.dll",
                "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path",
                "Tests failed.",
                Path.Combine(outputFolder, "02-ui-tests.log"),
                "Tests failed.",
                1));
        WriteValidationResultArtifact(failedResult);

        var semanticReuseService = new FixedSemanticReuseService(repoRoot, new SemanticReuseSuggestionSet(
            "local_only",
            "Loaded 1 similar past case. Qdrant was unavailable, so deterministic local ranking was used.",
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_design_note.md"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index.json"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index_linkage.json"),
            new[]
            {
                new SemanticReuseSuggestedCase(
                    "run-failed",
                    "Current validation failure",
                    "doc-001",
                    "validation_failure_record",
                    "Run UI tests",
                    "Tests failed on a prior run.",
                    "failed",
                    0.82d,
                    "High",
                    "same failing stage; similar first-failure text",
                    Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "old-run", "validation_result.json"),
                    Array.Empty<SemanticReuseArtifactLink>(),
                    "old-run",
                    ContextKind: "validation_failure")
            }));
        var settingsStore = new InMemoryValidationSettingsStore
        {
            Current = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true)
        };
        File.WriteAllText(failedResult.FirstFailureLogPath!, "Tests failed.");
        ValidationRunnerService.RefreshTrendArtifacts(repoRoot, settingsStore.Current);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new ValidationRunnerService(
                repoRoot,
                new ScriptedValidationCommandExecutor(
                    new Dictionary<string, ValidationCommandExecutionResult>(System.StringComparer.Ordinal)
                    {
                        ["build_ui"] = new(0, new[] { "Build succeeded." }),
                        ["ui_tests"] = new(1, new[] { "Tests failed." })
                    })),
            validationSettingsStore: settingsStore,
            semanticReuseService: semanticReuseService);

        try
        {
            await vm.RunFullValidationLoopCommand.ExecuteAsync();
            await vm.RefreshSimilarCasesCommand.ExecuteAsync();

            Assert.Single(vm.SemanticReuseSuggestions);
            Assert.Equal("Local ranked", vm.SemanticReuseBadge);
            Assert.Contains("Loaded 1 similar past case", vm.SemanticReuseSummary, System.StringComparison.Ordinal);
            Assert.Equal("Current validation failure", vm.SemanticReuseSuggestions[0].ContextLabel);
            Assert.Equal("High", vm.SemanticReuseSuggestions[0].RankingLabel);
            Assert.Contains("same failing stage", vm.SemanticReuseSuggestions[0].MatchExplanation, System.StringComparison.Ordinal);
            Assert.True(vm.HasValidationFollowupReuseSuggestionSummary);
            Assert.Contains("Similar case suggestion", vm.ValidationFollowupReuseSuggestionSummary, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_plan_surfaces_planning_contextual_similar_cases()
    {
        var repoRoot = CreateValidationRepoRoot();
        var semanticReuseService = new FixedSemanticReuseService(repoRoot, new SemanticReuseSuggestionSet(
            "local_only",
            "Loaded planning hints.",
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_design_note.md"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index.json"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index_linkage.json"),
            new[]
            {
                new SemanticReuseSuggestedCase(
                    "planning-001",
                    "Current planning context",
                    "doc-plan-001",
                    "generated_output_pattern",
                    "Semantic Planner generated output",
                    "Validation passed cleanly.",
                    "passed",
                    0.91d,
                    "High",
                    "exact linked history; same project scope",
                    Path.Combine(repoRoot, ".state", "projects", "planning", "runs", "generated-run", "generated_output_validation.json"),
                    Array.Empty<SemanticReuseArtifactLink>(),
                    "generated-run",
                    "planning",
                    new[]
                    {
                        new SemanticReuseMetadataField("project_name", "deterministic-workspace"),
                        new SemanticReuseMetadataField("failing_stage", "Running UI tests")
                    },
                    "Follow-on evidence: helpful 1, unchanged 0, regressed 0.")
            }));
        var settingsStore = new InMemoryValidationSettingsStore
        {
            Current = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true)
        };

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationSettingsStore: settingsStore,
            semanticReuseService: semanticReuseService);

        try
        {
            vm.EnableSemanticReuseSuggestions = true;
            vm.IntakeIntent = "Build validation assistant";

            await vm.GeneratePlanCommand.ExecuteAsync();

            Assert.Equal("Planning", vm.SelectedSemanticReuseContext);
            Assert.True(vm.HasVisibleSemanticReuseSuggestions);
            Assert.Equal("planning", vm.SemanticReuseSuggestions[0].ContextKind);
            Assert.Contains("Prior passing outputs", vm.SemanticReuseContextSummary, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Operator_playbooks_are_loaded_and_filtered_by_settings()
    {
        var repoRoot = CreateValidationRepoRoot();
        try
        {
            SeedPlanningPlaybookArtifacts(repoRoot);
            var settingsStore = new InMemoryValidationSettingsStore
            {
                Current = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true, false, true, true, true, 2, true, 3)
            };

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationSettingsStore: settingsStore,
                semanticReuseService: new SemanticReuseService(repoRoot));

            vm.SelectedSemanticReuseContext = "Planning";

            Assert.True(vm.HasVisibleSemanticReusePlaybooks);
            Assert.Single(vm.VisibleSemanticReusePlaybooks);
            Assert.Contains("playbook suggestion", vm.SemanticReusePlaybookSummary, System.StringComparison.Ordinal);

            vm.ShowTentativePlaybooks = false;

            Assert.False(vm.HasVisibleSemanticReusePlaybooks);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Attempt_repair_persists_selected_similar_case_references()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, ".state", "projects", "deterministic-project", "runs", "run-001");
        Directory.CreateDirectory(runPath);
        GeneratedOutputValidationLinkService.Save(new GeneratedOutputValidationLink(
            "run-001",
            runPath,
            runPath,
            "failed",
            "Generated output validation failed.",
            "Validate generated output",
            "validation-source",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-failed"),
            "Tests failed.",
            System.DateTimeOffset.UtcNow));

        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-failed");
        Directory.CreateDirectory(outputFolder);
        var failedResult = new ValidationRunResult(
            "run-failed",
            "Validate generated output",
            outputFolder,
            false,
            "Validation failed: Tests failed.",
            "Tests failed.",
            Path.Combine(outputFolder, "02-ui-tests.log"),
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            System.DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("ui_tests", "Running UI tests", "failed", "Tests failed.", Path.Combine(outputFolder, "02-ui-tests.log"), 1, 40)
            },
            "failed",
            "Failed",
            new ValidationFirstFailure("ui_tests", "Running UI tests", "Shoots.Runtime.Tests.dll", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", "Tests failed.", Path.Combine(outputFolder, "02-ui-tests.log"), "Tests failed.", 1));
        WriteValidationResultArtifact(failedResult);
        var repairedResult = new ValidationRunResult(
            "run-repaired",
            "Validate generated output",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-repaired"),
            true,
            "Validation passed (1 stage).",
            null,
            null,
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            System.DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("ui_tests", "Running UI tests", "passed", "Tests passed.", Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-repaired", "02-ui-tests.log"), 0, 20)
            });
        var repairService = new RecordingRepairAttemptService(repoRoot, new[] { Path.Combine(repoRoot, "src", "Generated.cs") });
        var semanticReuseService = new FixedSemanticReuseService(repoRoot, new SemanticReuseSuggestionSet(
            "local_only",
            "Loaded 1 similar past case.",
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_design_note.md"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index.json"),
            Path.Combine(repoRoot, ".codex", "validation-ui", "semantic_reuse_index_linkage.json"),
            new[]
            {
                new SemanticReuseSuggestedCase(
                    "run-failed",
                    "Current validation failure",
                    "doc-repair-001",
                    "repair_bundle_summary",
                    "Repair repair-001",
                    "Repair outcome improved.",
                    "improved",
                    0.86d,
                    "High",
                    "same failing stage",
                    Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", "repair-001", "repair_comparison.json"),
                    new[] { new SemanticReuseArtifactLink("Repair comparison", Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", "repair-001", "repair_comparison.json")) },
                    "run-001",
                    "validation_failure",
                    new[]
                    {
                        new SemanticReuseMetadataField("changed_file_names", "Generated.cs"),
                        new SemanticReuseMetadataField("repaired_validation_status", "passed"),
                        new SemanticReuseMetadataField("failing_stage", "Running UI tests")
                    },
                    string.Empty)
            }));

        var runner = new SequencedValidationRunnerService(repoRoot, new[] { failedResult, repairedResult });
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            repairAttemptService: repairService,
            semanticReuseService: semanticReuseService,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.EnableSemanticReuseSuggestions = true;
            SetPrivateField(vm, "_lastDemoRunPath", runPath);
            await vm.ValidateGeneratedOutputCommand.ExecuteAsync();
            Assert.True(vm.HasVisibleSemanticReuseSuggestions);
            vm.SemanticReuseSuggestions[0].IsSelectedForRepairReference = true;

            await vm.AttemptRepairCommand.ExecuteAsync();

            Assert.NotNull(repairService.BundlePath);
            var bundle = System.Text.Json.JsonSerializer.Deserialize<RepairBundle>(File.ReadAllText(repairService.BundlePath!));
            Assert.NotNull(bundle);
            Assert.Single(bundle!.ReferenceCases!);
            Assert.Equal("doc-repair-001", bundle.ReferenceCases![0].DocumentId);
            Assert.True(File.Exists(SemanticReuseService.UsefulnessPathForRepo(repoRoot)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_retry_classification_updates_confidence_surface()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-flaky");
        Directory.CreateDirectory(outputFolder);
        var stabilityPath = Path.Combine(outputFolder, "validation_stability.json");
        File.WriteAllText(stabilityPath, "{}");

        var runner = new DeterministicValidationRunnerService(
            repoRoot,
            new ValidationRunResult(
                "run-flaky",
                "Run full validation loop",
                outputFolder,
                true,
                "Validation passed after retry; flaky behavior suspected (2 stages).",
                "[xUnit.net 00:00:03.37]     Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [FAIL]",
                Path.Combine(outputFolder, "02-ui-tests.log"),
                System.DateTimeOffset.UtcNow.AddMinutes(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 25),
                    new ValidationStageResult("ui_tests", "Running UI tests", "passed", "Running UI tests flaky behavior suspected. First failure: [xUnit.net 00:00:03.37]     Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [FAIL]", Path.Combine(outputFolder, "02-ui-tests.log"), 0, 60, "flaky_suspected", 1, Path.Combine(outputFolder, "02-ui-tests.retry1.log"))
                },
                "flaky_suspected",
                "Flaky suspected",
                new ValidationFirstFailure(
                    "ui_tests",
                    "Running UI tests",
                    "C:\\dev\\Shoots\\src\\Runtime\\Shoots.Runtime.Tests\\bin\\Debug\\net8.0\\Shoots.Runtime.Tests.dll",
                    "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path",
                    "[xUnit.net 00:00:03.37]     Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [FAIL]",
                    Path.Combine(outputFolder, "02-ui-tests.log"),
                    "[xUnit.net 00:00:03.37]     Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [FAIL]",
                    1),
                new[]
                {
                    new ValidationRetryAudit(
                        "ui_tests",
                        "Running UI tests",
                        "dotnet test .\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj -c Debug -v minimal",
                        Path.Combine(outputFolder, "02-ui-tests.retry1.log"),
                        "passed",
                        "flaky_suspected",
                        "Passed on retry.",
                        0,
                        System.DateTimeOffset.UtcNow.AddSeconds(-15),
                        System.DateTimeOffset.UtcNow.AddSeconds(-10))
                },
                stabilityPath));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.RunFullValidationLoopCommand.ExecuteAsync();

            Assert.Equal("Flaky suspected", vm.ValidationStabilityBadge);
            Assert.True(vm.HasValidationStabilityArtifactPath);
            Assert.True(vm.HasValidationFirstFailure);
            Assert.Equal("Flaky suspected", vm.ValidationStageResults[1].StabilityLabel);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_trend_surface_loads_history_regression_and_artifact_paths()
    {
        var repoRoot = CreateValidationRepoRoot();
        SeedValidationHistoryLedger(
            repoRoot,
            new[]
            {
                ValidationHistoryEntryForUi("20260310-120000000Z-build-ui", "Build UI project", 0, "passed", "passed", "", "", "", false),
                ValidationHistoryEntryForUi("20260310-120100000Z-ui-tests", "Run UI tests", 1, "passed", "passed", "", "", "", false),
                ValidationHistoryEntryForUi("20260310-120200000Z-ui-tests", "Run UI tests", 2, "failed", "failed", "Tests failed.", "Running UI tests", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", false)
            });
        ValidationRunnerService.RefreshTrendArtifacts(repoRoot, new ValidationSettings(false, false, 5, false, false, false, 20, 3, false));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.Equal("Regression detected", vm.ValidationTrendBadge);
            Assert.True(vm.HasValidationStageHistory);
            Assert.True(vm.HasValidationHistoryLedgerPath);
            Assert.True(vm.HasValidationTrendArtifactPath);
            Assert.True(vm.HasValidationRegressionArtifactPath);
            Assert.Contains("Recent pass rate", vm.ValidationTrendSummaryText, System.StringComparison.Ordinal);
            Assert.Contains("Window 3", vm.ValidationRegressionSummaryText, System.StringComparison.Ordinal);
            Assert.Contains("Running UI tests", vm.ValidationStageHistory[0].StageOutcomeSummary, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Set_release_baseline_persists_active_baseline_and_ready_state()
    {
        var repoRoot = CreateValidationRepoRoot();
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-clean");
        Directory.CreateDirectory(outputFolder);

        var runner = new DeterministicValidationRunnerService(
            repoRoot,
            new ValidationRunResult(
                "run-clean",
                "Run full validation loop",
                outputFolder,
                true,
                "Validation passed (1 stage).",
                null,
                null,
                System.DateTimeOffset.UtcNow.AddMinutes(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 25)
                },
                "passed",
                "Passed cleanly"));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            await vm.RunFullValidationLoopCommand.ExecuteAsync();

            Assert.True(vm.SetReleaseBaselineCommand.CanExecute(null));

            await vm.SetReleaseBaselineCommand.ExecuteAsync();

            Assert.True(vm.HasValidationBaselineArtifactPath);
            Assert.True(vm.HasValidationBaselineHistoryArtifactPath);
            Assert.True(vm.HasValidationBaselineComparisonArtifactPath);
            Assert.Equal("Ready", vm.ValidationReleaseReadinessBadge);
            Assert.Contains("Active baseline run-clean", vm.ValidationBaselineSummaryText, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_readiness_surface_loads_baseline_comparison_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();
        var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 3, false, 5, false, true);
        var cleanOutput = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-clean");
        Directory.CreateDirectory(cleanOutput);
        var cleanResult = new ValidationRunResult(
            "run-clean",
            "Build UI project",
            cleanOutput,
            true,
            "Validation passed (1 stage).",
            null,
            null,
            System.DateTimeOffset.UtcNow.AddMinutes(-2),
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            new[]
            {
                new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(cleanOutput, "01-build-ui.log"), 0, 25)
            },
            "passed",
            "Passed cleanly");
        ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, cleanResult, settings);

        var retryOutput = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-retry");
        Directory.CreateDirectory(retryOutput);
        var retryResult = new ValidationRunResult(
            "run-retry",
            "Build UI project",
            retryOutput,
            true,
            "Validation passed after retry (1 stage).",
            "error CS1000: build failed",
            Path.Combine(retryOutput, "01-build-ui.log"),
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            System.DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded after retry.", Path.Combine(retryOutput, "01-build-ui.log"), 0, 35, "passed_on_retry", 1, Path.Combine(retryOutput, "01-build-ui.retry1.log"))
            },
            "passed_on_retry",
            "Passed after retry");
        WriteValidationResultArtifact(retryResult);
        ValidationRunnerService.RefreshReleaseBaselineArtifacts(repoRoot, settings, retryResult);

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            Assert.Equal("Ready with caution", vm.ValidationReleaseReadinessBadge);
            Assert.True(vm.HasValidationBaselineComparisonArtifactPath);
            Assert.True(vm.HasValidationBaselineStageChanges);
            Assert.Contains("retry drift", vm.ValidationBaselineComparisonSummaryText, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("after retry", vm.ValidationBaselineStageChanges[0].LatestOutcome, System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_generated_output_updates_linked_status_and_persists_linkage()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-linked");
        Directory.CreateDirectory(outputFolder);

        var runner = new DeterministicValidationRunnerService(
            repoRoot,
            new ValidationRunResult(
                "run-linked",
                "Validate generated output",
                outputFolder,
                true,
                "Validation passed (1 stage).",
                null,
                null,
                System.DateTimeOffset.UtcNow.AddMinutes(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 25)
                }));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            await vm.ValidateGeneratedOutputCommand.ExecuteAsync();

            Assert.Equal("passed", vm.GeneratedOutputValidationStatus);
            Assert.Equal("Passed", vm.GeneratedOutputValidationBadge);
            Assert.Equal("Validated", vm.GeneratedOutputTrustBadge);
            Assert.True(File.Exists(GeneratedOutputValidationLinkService.PathForRun(runPath)));
            var link = GeneratedOutputValidationLinkService.Load(runPath);
            Assert.Equal("run-linked", link.ValidationRunId);
            Assert.Equal("passed", link.ValidationStatus);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Attempt_repair_is_disabled_while_generated_output_validation_is_running()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        var runner = new BlockingValidationRunnerService(repoRoot);
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            var task = vm.ValidateGeneratedOutputCommand.ExecuteAsync();
            await runner.WaitForStartAsync();

            Assert.False(vm.AttemptRepairCommand.CanExecute(null));
            Assert.Contains("Repair is unavailable while", vm.AttemptRepairDisabledReason);

            runner.Release();
            await task;
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Attempt_repair_writes_bundle_and_updates_change_review()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        var failedOutputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-failed");
        Directory.CreateDirectory(failedOutputFolder);
        var passedOutputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-passed");
        Directory.CreateDirectory(passedOutputFolder);

        var validationRunner = new SequencedValidationRunnerService(
            repoRoot,
            new[]
            {
                new ValidationRunResult(
                    "run-failed",
                    "Validate generated output",
                    failedOutputFolder,
                    false,
                    "Validation failed: Tests failed.",
                    "Tests failed.",
                    Path.Combine(failedOutputFolder, "01-ui-tests.log"),
                    System.DateTimeOffset.UtcNow.AddMinutes(-2),
                    System.DateTimeOffset.UtcNow.AddMinutes(-1),
                    new[]
                    {
                        new ValidationStageResult("ui_tests", "Running UI tests", "failed", "Tests failed.", Path.Combine(failedOutputFolder, "01-ui-tests.log"), 1, 50)
                    }),
                new ValidationRunResult(
                    "run-passed",
                    "Validate generated output",
                    passedOutputFolder,
                    true,
                    "Validation passed (1 stage).",
                    null,
                    null,
                    System.DateTimeOffset.UtcNow.AddMinutes(-1),
                    System.DateTimeOffset.UtcNow,
                    new[]
                    {
                        new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(passedOutputFolder, "01-build-ui.log"), 0, 25)
                    })
            });
        var repairService = new RecordingRepairAttemptService(repoRoot, new[] { Path.Combine(repoRoot, "src", "Generated.cs") });

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: validationRunner,
            validationSettingsStore: new InMemoryValidationSettingsStore(),
            repairAttemptService: repairService);

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            await vm.ValidateGeneratedOutputCommand.ExecuteAsync();
            Assert.Equal("failed", vm.GeneratedOutputValidationStatus);

            await vm.AttemptRepairCommand.ExecuteAsync();

            Assert.True(File.Exists(repairService.BundlePath!));
            Assert.True(vm.HasRepairBundlePath);
            Assert.True(vm.HasRepairChangedFiles);
            Assert.True(vm.HasRepairHistory);
            Assert.Contains(Path.Combine(repoRoot, "src", "Generated.cs"), vm.RepairChangedFiles);
            Assert.Equal("passed", vm.RepairOutcome);
            Assert.Equal("Repaired", vm.GeneratedOutputTrustBadge);
            Assert.Equal("Running UI tests", vm.RepairComparisonSourceStage);
            Assert.Equal("Tests failed.", vm.RepairComparisonSourceExcerpt);
            Assert.Contains("Validation passed", vm.RepairComparisonValidationResult, System.StringComparison.Ordinal);
            Assert.Contains("passed", vm.RepairSummary, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("passed", vm.GeneratedOutputValidationStatus);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Promote_repair_result_persists_metadata_for_improved_or_passed_repairs()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedRepairReviewArtifacts(runPath, repoRoot, "repair-001", "passed");

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            vm.RepairReviewNote = "operator approved";

            Assert.True(vm.PromoteRepairResultCommand.CanExecute(null));

            await vm.PromoteRepairResultCommand.ExecuteAsync();

            var promotion = RepairReviewArtifactsService.LoadPromotion(runPath);
            Assert.NotNull(promotion);
            Assert.Equal("repair-001", promotion!.RepairId);
            Assert.Equal("promoted_from_repair", promotion.Status);
            Assert.Equal("operator approved", promotion.OperatorNote);
            Assert.Equal("promoted_from_repair", vm.RepairPromotionStatus);
            Assert.Equal("Promoted from repair", vm.RepairPromotionBadge);
            Assert.Equal("Promoted", vm.GeneratedOutputTrustBadge);
            Assert.Equal("repair-001", vm.PromotedRepairId);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Adopt_and_unadopt_repair_update_trust_and_persist_notes()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedPromotedRepairArtifacts(runPath, repoRoot, "repair-001", "passed");

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            vm.RepairReviewNote = "merged into working tree";
            await vm.AdoptRepairCommand.ExecuteAsync();

            var adopted = RepairReviewArtifactsService.LoadPromotion(runPath);
            Assert.NotNull(adopted);
            Assert.Equal("adopted", adopted!.AdoptionState);
            Assert.Equal("merged into working tree", adopted.OperatorNote);
            Assert.Equal("Adopted", vm.RepairAdoptionBadge);
            Assert.Equal("Adopted", vm.GeneratedOutputTrustBadge);

            vm.RepairReviewNote = "rolled back after review";
            await vm.UnadoptRepairCommand.ExecuteAsync();

            var rolledBack = RepairReviewArtifactsService.LoadPromotion(runPath);
            Assert.NotNull(rolledBack);
            Assert.Equal("rolled_back", rolledBack!.AdoptionState);
            Assert.Equal("rolled back after review", rolledBack.OperatorNote);
            Assert.Equal("No longer current", vm.RepairAdoptionBadge);
            Assert.Equal("Superseded", vm.GeneratedOutputTrustBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Replace_repair_marks_state_replaced_by_newer_output()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedPromotedRepairArtifacts(runPath, repoRoot, "repair-001", "improved");

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            vm.RepairReviewNote = "replaced by fresh generation";
            await vm.ReplaceRepairCommand.ExecuteAsync();

            var promotion = RepairReviewArtifactsService.LoadPromotion(runPath);
            Assert.NotNull(promotion);
            Assert.Equal("replaced_by_newer_output", promotion!.AdoptionState);
            Assert.Equal("Replaced by newer output", vm.RepairAdoptionBadge);
            Assert.Equal("Superseded", vm.GeneratedOutputTrustBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repair_navigation_commands_open_promoted_and_audit_paths()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedPromotedRepairArtifacts(runPath, repoRoot, "repair-001", "passed");
        var shell = new RecordingWorkspaceShellService();

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore(),
            workspaceShell: shell);

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");

            await vm.OpenPromotedRepairFolderCommand.ExecuteAsync();
            await vm.OpenRepairAuditSummaryFolderCommand.ExecuteAsync();
            await vm.OpenLinkedRepairValidationRunFolderCommand.ExecuteAsync();

            Assert.Equal(3, shell.OpenedPaths.Count);
            Assert.Contains(Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", "repair-001"), shell.OpenedPaths);
            Assert.Contains(Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", "repair-001", "audit"), shell.OpenedPaths);
            Assert.Contains(Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-repair-001"), shell.OpenedPaths);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Promote_repair_result_is_blocked_for_unchanged_repairs()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedRepairReviewArtifacts(runPath, repoRoot, "repair-001", "unchanged");

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");

            Assert.False(vm.PromoteRepairResultCommand.CanExecute(null));
            Assert.Contains("improved or passed", vm.PromoteRepairDisabledReason, System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Promote_repair_result_is_disabled_while_validation_is_running()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedRepairReviewArtifacts(runPath, repoRoot, "repair-001", "passed");

        var runner = new BlockingValidationRunnerService(repoRoot);
        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: runner,
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");
            var task = vm.RunFullValidationLoopCommand.ExecuteAsync();
            await runner.WaitForStartAsync();

            Assert.False(vm.PromoteRepairResultCommand.CanExecute(null));
            Assert.Contains("Promotion is unavailable while", vm.PromoteRepairDisabledReason, System.StringComparison.Ordinal);

            runner.Release();
            await task;
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Promotion_status_is_superseded_when_a_later_repair_exists()
    {
        var repoRoot = CreateValidationRepoRoot();
        var runPath = Path.Combine(repoRoot, "generated-run");
        Directory.CreateDirectory(runPath);
        SeedRepairReviewArtifacts(runPath, repoRoot, "repair-002", "improved");
        RepairReviewArtifactsService.SavePromotion(
            runPath,
            new RepairPromotionRecord(
                "run-001",
                runPath,
                "repair-001",
                "source-run-001",
                "validation-repair-001",
                "passed",
                "passed_validation",
                "Passed validation after repair.",
                "promoted_from_repair",
                "Repair outcome passed.",
                "promoted_only",
                "Promoted repair is recorded but not yet adopted into the current working output.",
                string.Empty,
                Path.Combine(runPath, ".codex", "validation-ui", "repairs", "repair-001", "repair_bundle.json"),
                Path.Combine(runPath, ".codex", "validation-ui", "repairs", "repair-001"),
                Path.Combine(runPath, ".codex", "validation-ui", "runs", "validation-repair-001"),
                string.Empty,
                string.Empty,
                string.Empty,
                System.DateTimeOffset.UtcNow.AddMinutes(-2),
                System.DateTimeOffset.UtcNow.AddMinutes(-2)));

        var vm = BuildViewModel(
            new FixedBackendProbeService(
                new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
            validationRunnerService: new DeterministicValidationRunnerService(repoRoot, SuccessfulValidationResult(repoRoot)),
            validationSettingsStore: new InMemoryValidationSettingsStore());

        try
        {
            vm.SelectedRunHistory = new MainWindowViewModel.RunHistoryRow("run-001", runPath, System.DateTimeOffset.UtcNow, "Completed", "ollama", "none", "Verified");

            Assert.Equal("superseded_by_later_repair", vm.RepairPromotionStatus);
            Assert.Equal("Superseded by later repair", vm.RepairPromotionBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_proof_panel_loads_latest_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal(BuilderExecutionService.BuilderProofFloorModelId, vm.BuilderProofModelId);
            Assert.Equal("Passed with routing", vm.BuilderProofOutcomeBadge);
            Assert.Equal("Passed cleanly", vm.BuilderExternalProofOutcomeBadge);
            Assert.True(vm.HasBuilderProofSummary);
            Assert.True(vm.HasBuilderProofSummaryPath);
            Assert.True(vm.HasBuilderExternalProofSummary);
            Assert.True(vm.HasBuilderExternalProofSummaryPath);
            Assert.True(vm.HasBuilderProofSuccessCountsSummary);
            Assert.True(vm.HasBuilderModelFloorVerdictSummary);
            Assert.True(vm.HasBuilderModelFloorVerdictPath);
            Assert.True(vm.HasBuilderExternalFloorVerdictSummary);
            Assert.True(vm.HasBuilderModelFloorFailurePatternSummary);
            Assert.True(vm.HasBuilderModelFloorFailurePatternsPath);
            Assert.True(vm.HasBuilderModelFloorGuidanceSummary);
            Assert.True(vm.HasBuilderModelFloorGuidancePath);
            Assert.True(vm.HasBuilderModelTrustBandSummary);
            Assert.True(vm.HasBuilderModelTrustBandsPath);
            Assert.True(vm.HasBuilderModelScopeSummary);
            Assert.True(vm.HasBuilderModelScopeSummaryPath);
            Assert.True(vm.HasBuilderModelRoutingRecommendationSummary);
            Assert.True(vm.HasBuilderModelRoutingRecommendationPath);
            Assert.True(vm.HasBuilderModelWeakSpotSummary);
            Assert.True(vm.HasBuilderModelEscalationSummary);
            Assert.True(vm.HasBuilderModelEscalationDecisionPath);
            Assert.True(vm.HasBuilderModelRoutingPlanSummary);
            Assert.True(vm.HasBuilderModelRoutingPlanPath);
            Assert.True(vm.HasBuilderModelSplitTaskGuidanceSummary);
            Assert.True(vm.HasBuilderModelRoutingWeakSpotReason);
            Assert.True(vm.HasBuilderStrongerTierAvailabilitySummary);
            Assert.True(vm.HasBuilderStrongerTierAvailabilityPath);
            Assert.Equal("Reject band", vm.BuilderProofTrustBandBadge);
            Assert.Equal("Out of scope for low-floor model", vm.BuilderRoutingRecommendationBadge);
            Assert.Equal("Split task before using low-floor model", vm.BuilderModelEscalationBadge);
            Assert.Equal("Stronger tier available", vm.BuilderStrongerTierAvailabilityBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Run_builder_proof_command_populates_latest_proof_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.False(vm.HasBuilderProofSummaryPath);
            Assert.True(vm.RunBuilderProofMatrixCommand.CanExecute(null));

            await vm.RunBuilderProofMatrixCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderProofSummaryPath);
            Assert.True(vm.HasBuilderModelFloorVerdictPath);
            Assert.True(vm.HasBuilderExternalProofSummaryPath);
            Assert.True(vm.HasBuilderModelFloorFailurePatternsPath);
            Assert.True(vm.HasBuilderModelFloorGuidancePath);
            Assert.True(vm.HasBuilderModelTrustBandsPath);
            Assert.True(vm.HasBuilderModelScopeSummaryPath);
            Assert.True(vm.HasBuilderModelRoutingRecommendationPath);
            Assert.True(vm.HasBuilderModelEscalationDecisionPath);
            Assert.True(vm.HasBuilderModelRoutingPlanPath);
            Assert.True(vm.HasBuilderStrongerTierAvailabilityPath);
            Assert.Equal("Passed with routing", vm.BuilderProofOutcomeBadge);
            Assert.Equal("Passed cleanly", vm.BuilderExternalProofOutcomeBadge);
            Assert.Equal("Sufficient with repair loop", vm.BuilderModelFloorVerdictBadge);
            Assert.Equal("Sufficient for bounded external targets", vm.BuilderExternalFloorVerdictBadge);
            Assert.Equal("Reject band", vm.BuilderProofTrustBandBadge);
            Assert.Equal("Out of scope for low-floor model", vm.BuilderRoutingRecommendationBadge);
            Assert.Equal("Split task before using low-floor model", vm.BuilderModelEscalationBadge);
            Assert.Equal("Stronger tier available", vm.BuilderStrongerTierAvailabilityBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_comparative_proof_panel_loads_latest_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Stronger tier available", vm.BuilderStrongerTierAvailabilityBadge);
            Assert.Equal("Cleaner success", vm.BuilderComparativeProofBadge);
            Assert.Equal("Split first, keep low-floor", vm.BuilderRoutingPolicyBadge);
            Assert.Equal("Low floor if split first", vm.BuilderTieredRoutingBadge);
            Assert.True(vm.HasBuilderComparativeProofSummary);
            Assert.True(vm.HasBuilderComparativeProofSummaryPath);
            Assert.True(vm.HasBuilderComparativeRepairBurdenSummary);
            Assert.True(vm.HasBuilderRoutingPolicySummary);
            Assert.True(vm.HasBuilderRoutingPolicyPath);
            Assert.True(vm.HasBuilderSplitFirstPlanSummary);
            Assert.True(vm.HasBuilderSplitFirstPlanPath);
            Assert.True(vm.HasBuilderTieredRoutingSummary);
            Assert.True(vm.HasBuilderTieredRoutingPath);
            Assert.True(vm.HasBuilderPrimaryRoutingRecommendationSummary);
            Assert.True(vm.HasBuilderStrongerTierRoleSummary);
            Assert.True(vm.HasBuilderWeakSpotMitigationSummary);
            Assert.Contains("split", vm.BuilderPrimaryRoutingRecommendationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cleaner", vm.BuilderStrongerTierRoleSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_split_step_panel_derives_next_step_state_and_runs_first_interaction()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderSplitStepExecutionSummary);
            Assert.True(vm.HasBuilderSplitSteps);
            Assert.Equal(3, vm.BuilderSplitSteps.Count);
            Assert.Equal("View ready", vm.BuilderSplitSteps[0].ExecutionAvailability);
            Assert.Equal("Not started", vm.BuilderSplitSteps[0].CompletionBadge);
            Assert.Contains("must finish", vm.BuilderSplitSteps[1].BlockReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.RunNextBuilderSplitStepCommand.CanExecute(null));

            await vm.RunNextBuilderSplitStepCommand.ExecuteAsync();

            Assert.Single(shell.OpenedPaths);
            Assert.Contains("split-execution-hooks", shell.OpenedPaths[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Opened", vm.BuilderSplitSteps[0].CompletionBadge);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_split_step_helpers_route_expected_paths_after_split_execution()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderSplitFirstExecutionAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Split matched stronger tier", vm.BuilderSplitFirstOutcomeBadge);
            Assert.True(vm.HasBuilderSplitFirstOutcomeSummary);
            Assert.True(vm.HasBuilderSplitStepExecutionPath);
            Assert.True(vm.HasBuilderSplitFirstOutcomePath);
            Assert.Equal("Opened", vm.BuilderSplitSteps[0].CompletionBadge);
            Assert.Equal("Executed", vm.BuilderSplitSteps[1].CompletionBadge);
            Assert.Equal("Completed by outcome", vm.BuilderSplitSteps[2].CompletionBadge);
            Assert.Contains("already completed", vm.BuilderSplitExecutionDisabledReason, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderSplitStepExecutionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderSplitFirstOutcomeCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderSplitExecutionSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderSplitComparativeClosureSummaryCommand.CanExecute(null));

            await vm.OpenBuilderSplitStepExecutionCommand.ExecuteAsync();
            await vm.OpenBuilderSplitFirstOutcomeCommand.ExecuteAsync();
            await vm.CopyBuilderSplitExecutionSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderSplitComparativeClosureSummaryCommand.ExecuteAsync();

            Assert.Equal(2, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderSplitStepExecutionPath(run.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderSplitFirstOutcomePath(run.RunFolder), shell.OpenedPaths[1]);
            Assert.Equal(2, shell.CopiedTexts.Count);
            Assert.Contains("split-step execution recorded", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stronger-tier", shell.CopiedTexts[1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_default_guidance_surface_loads_and_helpers_open_expected_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderSplitFirstExecutionAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Split-first low-floor", vm.BuilderDefaultGuidanceBadge);
            Assert.Equal("Provisional", vm.BuilderGuidanceSupportBadge);
            Assert.True(vm.HasBuilderDefaultGuidanceSummary);
            Assert.True(vm.HasBuilderDefaultGuidancePath);
            Assert.True(vm.HasBuilderGuidanceHistoryPath);
            Assert.True(vm.HasBuilderLatestRoutingDecisionSummary);
            Assert.True(vm.HasBuilderLatestRoutingDecisionPath);
            Assert.True(vm.HasBuilderGuidanceSupportSummary);
            Assert.True(vm.HasBuilderGuidanceSupportPath);
            Assert.Contains("split-first", vm.BuilderDefaultGuidanceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bounded_refactor", vm.BuilderLatestRoutingDecisionSummary, StringComparison.Ordinal);

            Assert.True(vm.OpenBuilderDefaultGuidanceCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderGuidanceHistoryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderLatestRoutingDecisionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderGuidanceSupportCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderDefaultGuidanceSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderLatestRoutingDecisionCommand.CanExecute(null));

            await vm.OpenBuilderDefaultGuidanceCommand.ExecuteAsync();
            await vm.OpenBuilderGuidanceHistoryCommand.ExecuteAsync();
            await vm.OpenBuilderLatestRoutingDecisionCommand.ExecuteAsync();
            await vm.OpenBuilderGuidanceSupportCommand.ExecuteAsync();
            await vm.CopyBuilderDefaultGuidanceSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderLatestRoutingDecisionCommand.ExecuteAsync();

            Assert.Equal(4, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderDefaultPolicyHistoryPathForRepo(repoRoot), shell.OpenedPaths[1]);
            Assert.Equal(BuilderExecutionService.BuilderRequestPolicyDecisionPath(run.RunFolder), shell.OpenedPaths[2]);
            Assert.Equal(BuilderExecutionService.BuilderPolicyStabilityPath(run.RunFolder), shell.OpenedPaths[3]);
            Assert.Equal(2, shell.CopiedTexts.Count);
            Assert.Contains("default builder model", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("split_first_low_floor", shell.CopiedTexts[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_default_guidance_support_badge_tracks_repeated_proof_runs()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderSplitFirstExecutionAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderSplitFirstExecutionAsync(repoRoot, provider: "ollama");

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Corroborated", vm.BuilderGuidanceSupportBadge);
            Assert.Contains("2 supporting proof run", vm.BuilderGuidanceSupportSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_intake_and_execution_prep_surface_loads_and_helpers_open_expected_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.RunBuilderSplitFirstExecutionAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Ready through split-first prep", vm.BuilderIntakeBadge);
            Assert.Equal("Split-first route", vm.BuilderPrepRouteBadge);
            Assert.True(vm.HasBuilderIntakeSummary);
            Assert.True(vm.HasBuilderIntakePath);
            Assert.True(vm.HasBuilderPrepSummary);
            Assert.True(vm.HasBuilderPrepPath);
            Assert.Contains("Support=provisional", vm.BuilderIntakeSummary, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("split_first_low_floor_route", vm.BuilderPrepSummary, System.StringComparison.Ordinal);

            Assert.True(vm.OpenBuilderRequestIntakeCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderExecutionPrepCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderIntakeRoutingSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderExecutionPrepSummaryCommand.CanExecute(null));

            await vm.OpenBuilderRequestIntakeCommand.ExecuteAsync();
            await vm.OpenBuilderExecutionPrepCommand.ExecuteAsync();
            await vm.CopyBuilderIntakeRoutingSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderExecutionPrepSummaryCommand.ExecuteAsync();

            Assert.Equal(2, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderRequestIntakePath(run.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderExecutionPrepPath(run.RunFolder), shell.OpenedPaths[1]);
            Assert.Equal(2, shell.CopiedTexts.Count);
            Assert.Contains("ready_for_split_first_low_floor", shell.CopiedTexts[0], System.StringComparison.Ordinal);
            Assert.Contains("split_first_low_floor_route", shell.CopiedTexts[1], System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_prepared_launch_surface_runs_and_helpers_open_expected_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.LaunchPreparedBuilderRouteCommand.CanExecute(null));
            Assert.Equal("Ready to launch", vm.BuilderLaunchAvailabilityBadge);
            Assert.False(vm.HasBuilderLaunchSummary);
            Assert.False(vm.HasBuilderResultSummary);

            await vm.LaunchPreparedBuilderRouteCommand.ExecuteAsync();

            Assert.Equal("Already launched", vm.BuilderLaunchAvailabilityBadge);
            Assert.Equal("Launched and passed", vm.BuilderResultBadge);
            Assert.Equal("Prep confirmed", vm.BuilderRouteComparisonBadge);
            Assert.True(vm.HasBuilderLaunchSummary);
            Assert.True(vm.HasBuilderLaunchPath);
            Assert.True(vm.HasBuilderResultSummary);
            Assert.True(vm.HasBuilderResultPath);
            Assert.Contains("already has a recorded route result", vm.BuilderPreparedLaunchDisabledReason, System.StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderExecutionLaunchCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderExecutionResultCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderExecutionLaunchSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderExecutionResultSummaryCommand.CanExecute(null));

            await vm.OpenBuilderExecutionLaunchCommand.ExecuteAsync();
            await vm.OpenBuilderExecutionResultCommand.ExecuteAsync();
            await vm.CopyBuilderExecutionLaunchSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderExecutionResultSummaryCommand.ExecuteAsync();

            Assert.Equal(2, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderExecutionLaunchPath(run.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderExecutionResultPath(run.RunFolder), shell.OpenedPaths[1]);
            Assert.Equal(2, shell.CopiedTexts.Count);
            Assert.Contains("split_first_low_floor_route", shell.CopiedTexts[0], System.StringComparison.Ordinal);
            Assert.Contains("launched on split_first_low_floor_route", shell.CopiedTexts[1], System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_default_launch_surface_records_override_evidence_and_routes_helpers()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var latestRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.LaunchBuilderOverrideRouteCommand.CanExecute(null));
            Assert.True(vm.HasBuilderOverrideRouteOptionSummary);
            Assert.Contains("direct_low_floor_route", vm.BuilderOverrideRouteOptionSummary, StringComparison.Ordinal);

            await vm.LaunchBuilderOverrideRouteCommand.ExecuteAsync();

            Assert.Equal("Already launched", vm.BuilderLaunchAvailabilityBadge);
            Assert.True(vm.HasBuilderLaunchDefaultDecisionSummary);
            Assert.True(vm.HasBuilderLaunchDefaultDecisionPath);
            Assert.True(vm.HasBuilderLaunchRouteModeSummary);
            Assert.True(vm.HasBuilderRouteOverrideSummary);
            Assert.True(vm.HasBuilderRouteOverridePath);
            Assert.True(vm.HasBuilderRouteReviewSummary);
            Assert.True(vm.HasBuilderRouteReviewPath);
            Assert.True(vm.HasBuilderRouteReconfirmationSummary);
            Assert.True(vm.HasBuilderRouteReconfirmationPath);
            Assert.True(vm.HasBuilderDefaultRouteRecoverySummary);
            Assert.True(vm.HasBuilderDefaultRouteRecoveryPath);
            Assert.Contains("overridden_by_operator", vm.BuilderLaunchDefaultDecisionSummary, StringComparison.Ordinal);
            Assert.Contains("clean bounded launch", vm.BuilderLaunchRouteModeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regressed outcome", vm.BuilderRouteOverrideSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stable default", vm.BuilderRouteReviewSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("override_route_failure", vm.BuilderRouteReconfirmationSummary, StringComparison.Ordinal);
            Assert.Contains("still suspended", vm.BuilderDefaultRouteRecoverySummary, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderLaunchDefaultDecisionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRouteOverrideEvidenceCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRouteReviewCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRouteReconfirmationCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderDefaultRouteRecoveryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderLaunchDefaultSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderRouteOverrideSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderRouteReconfirmationSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderDefaultRouteRecoverySummaryCommand.CanExecute(null));

            await vm.OpenBuilderLaunchDefaultDecisionCommand.ExecuteAsync();
            await vm.OpenBuilderRouteOverrideEvidenceCommand.ExecuteAsync();
            await vm.OpenBuilderRouteReviewCommand.ExecuteAsync();
            await vm.OpenBuilderRouteReconfirmationCommand.ExecuteAsync();
            await vm.OpenBuilderDefaultRouteRecoveryCommand.ExecuteAsync();
            await vm.CopyBuilderLaunchDefaultSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderRouteOverrideSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderRouteReconfirmationSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderDefaultRouteRecoverySummaryCommand.ExecuteAsync();

            Assert.Equal(5, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderLaunchDefaultDecisionPath(latestRun.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderRouteOverrideEvidencePath(latestRun.RunFolder), shell.OpenedPaths[1]);
            Assert.Equal(BuilderExecutionService.BuilderPolicyReviewCandidatesPath(latestRun.RunFolder), shell.OpenedPaths[2]);
            Assert.Equal(BuilderExecutionService.BuilderRouteReconfirmationPath(latestRun.RunFolder), shell.OpenedPaths[3]);
            Assert.Equal(BuilderExecutionService.BuilderDefaultRouteRecoveryPath(latestRun.RunFolder), shell.OpenedPaths[4]);
            Assert.Equal(4, shell.CopiedTexts.Count);
            Assert.Contains("overridden_by_operator", shell.CopiedTexts[0], StringComparison.Ordinal);
            Assert.Contains("regressed outcome", shell.CopiedTexts[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("override_route_failure", shell.CopiedTexts[2], StringComparison.Ordinal);
            Assert.Contains("still suspended", shell.CopiedTexts[3], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_route_current_state_surface_uses_repo_level_authoritative_paths_after_newer_proof_run()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var overrideRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(
                repoRoot,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Authoritative VM path test.");

            var latestProofOnlyRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.False(vm.HasBuilderLaunchPath);
            Assert.False(vm.HasBuilderResultPath);
            Assert.True(vm.HasBuilderLaunchDefaultDecisionPath);
            Assert.True(vm.HasBuilderRouteOverridePath);
            Assert.True(vm.HasBuilderRouteReviewPath);
            Assert.True(vm.HasBuilderRouteReconfirmationPath);
            Assert.True(vm.HasBuilderRouteContinuitySummary);
            Assert.True(vm.HasBuilderRouteContinuityPath);
            Assert.True(vm.HasBuilderRouteCurrentStateIndexSummary);
            Assert.True(vm.HasBuilderRouteCurrentStateIndexPath);
            Assert.Contains("carried forward", vm.BuilderRouteCurrentStateIndexSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("override", vm.BuilderRouteContinuitySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BuilderExecutionService.BuilderLaunchDefaultDecisionPath(overrideRun.RunFolder), vm.BuilderLaunchDefaultDecisionPath);
            Assert.Equal(BuilderExecutionService.BuilderRouteOverrideEvidencePath(overrideRun.RunFolder), vm.BuilderRouteOverridePath);
            Assert.Equal(BuilderExecutionService.BuilderPolicyReviewCandidatesPath(overrideRun.RunFolder), vm.BuilderRouteReviewPath);
            Assert.Equal(BuilderExecutionService.BuilderRouteReconfirmationPath(latestProofOnlyRun.RunFolder), vm.BuilderRouteReconfirmationPath);

            Assert.True(vm.OpenBuilderRouteContinuityCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRouteCurrentStateIndexCommand.CanExecute(null));

            await vm.OpenBuilderRouteContinuityCommand.ExecuteAsync();
            await vm.OpenBuilderRouteCurrentStateIndexCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderRouteStateContinuityPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderRouteCurrentStateIndexPathForRepo(repoRoot), shell.OpenedPaths);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_toolchain_readiness_surface_shows_capability_refresh_and_block_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoLanguagePolicyFiles(repoRoot);
            var builderService = CreateBuilderExecutionService(
                new SuccessfulBuilderProofCommandRunner(),
                capabilityScanner: new ScriptedBuilderToolchainCapabilityScanner(
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        string.Empty,
                        string.Empty,
                        false,
                        false,
                        "not_found",
                        "dotnet is not installed.",
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", CultureInfo.InvariantCulture)),
                    new BuilderToolchainCapabilityObservation(
                        "node",
                        "runtime",
                        @"C:\tools\node\node.exe",
                        "22.5.1",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", CultureInfo.InvariantCulture))));

            var latestRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderToolchainReadinessSummary);
            Assert.True(vm.HasBuilderLanguageEligibilitySummary);
            Assert.True(vm.HasBuilderCapabilityRoutingSummary);
            Assert.True(vm.HasBuilderCapabilityBlockDecisionPath);
            Assert.Contains("WPF/Desktop .NET", vm.BuilderToolchainReadinessSummary, StringComparison.Ordinal);
            Assert.Contains("dotnet", vm.BuilderToolchainReadinessSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blocked", vm.BuilderLanguageEligibilitySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("route blocked", vm.BuilderCapabilityRoutingSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("toolchain", vm.BuilderPreparedLaunchDisabledReason, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderToolchainCapabilityRegistryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderLanguageEligibilityCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderCapabilityBlockDecisionCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderToolchainReadinessSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderLanguageEligibilitySummaryCommand.CanExecute(null));

            await vm.OpenBuilderToolchainCapabilityRegistryCommand.ExecuteAsync();
            await vm.OpenBuilderLanguageEligibilityCommand.ExecuteAsync();
            await vm.OpenBuilderCapabilityBlockDecisionCommand.ExecuteAsync();
            await vm.CopyBuilderToolchainReadinessSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderLanguageEligibilitySummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderToolchainCapabilityRegistryPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderLanguageEligibilityPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderCapabilityBlockDecisionPath(latestRun.RunFolder), shell.OpenedPaths);
            Assert.Contains("WPF/Desktop .NET", shell.CopiedTexts[0], StringComparison.Ordinal);
            Assert.Contains("default", shell.CopiedTexts[1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_repo_knowledge_surface_shows_summary_and_helpers()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoKnowledgeFiles(repoRoot);
            var builderService = CreateBuilderExecutionService();
            builderService.RefreshBuilderCapabilityArtifacts(repoRoot);
            builderService.RefreshBuilderRepoKnowledgeArtifacts(repoRoot);

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderRepoKnowledgeSummary);
            Assert.True(vm.HasBuilderRepoKnowledgeIndexPath);
            Assert.True(vm.HasBuilderRepoKnowledgeSummaryPath);
            Assert.Contains("WPF/Desktop .NET", vm.BuilderRepoKnowledgeSummary, StringComparison.Ordinal);
            Assert.Contains("builder", vm.BuilderRepoKnowledgeSummary, StringComparison.OrdinalIgnoreCase);

            await vm.OpenBuilderRepoKnowledgeIndexCommand.ExecuteAsync();
            await vm.OpenBuilderRepoKnowledgeSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderRepoKnowledgeSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderRepoKnowledgeIndexPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderRepoKnowledgeSummaryPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("WPF/Desktop .NET", shell.CopiedTexts[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_conversation_surface_shows_preview_handoff_and_weak_match_guardrail()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoKnowledgeFiles(repoRoot);
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            vm.BuilderConversationRequestText = "Do the thing.";
            await vm.PreviewBuilderConversationCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderConversationTaskSummary);
            Assert.True(vm.HasBuilderConversationRepoMatchSummary);
            Assert.True(vm.HasBuilderConversationRouteSummary);
            Assert.True(vm.HasBuilderConversationIntakePath);
            Assert.Contains("weak", vm.BuilderConversationRepoMatchSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("operator", vm.BuilderPreparedLaunchDisabledReason, StringComparison.OrdinalIgnoreCase);

            vm.BuilderConversationSelectedOverrideRoute = "direct_low_floor_route";
            await vm.OverrideBuilderConversationCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderConversationHandoffPath);
            Assert.DoesNotContain("weak", vm.BuilderPreparedLaunchDisabledReason, StringComparison.OrdinalIgnoreCase);

            await vm.OpenBuilderRepoRetrievalContextCommand.ExecuteAsync();
            await vm.OpenBuilderConversationIntakeCommand.ExecuteAsync();
            await vm.OpenBuilderConversationHandoffCommand.ExecuteAsync();
            await vm.CopyBuilderConversationRouteSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderRepoRetrievalContextPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderConversationIntakePathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderConversationHandoffPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("route", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_conversation_execution_surface_shows_session_patch_review_and_acceptance()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoKnowledgeFiles(repoRoot);
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            vm.BuilderConversationRequestText = "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.";
            await vm.PreviewBuilderConversationCommand.ExecuteAsync();
            await vm.AcceptBuilderConversationCommand.ExecuteAsync();
            await vm.ExecuteBuilderConversationSessionCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderConversationExecutionSessionSummary);
            Assert.True(vm.HasBuilderConversationPatchReviewSummary);
            Assert.True(vm.HasBuilderConversationReviewStateSummary);
            Assert.True(vm.HasBuilderConversationExecutionSessionPath);
            Assert.True(vm.HasBuilderConversationPatchReviewPath);
            Assert.Contains("awaiting", vm.BuilderConversationExecutionSessionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("changed", vm.BuilderConversationPatchReviewSummary, StringComparison.OrdinalIgnoreCase);

            await vm.AcceptBuilderConversationPatchReviewCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderConversationPatchReviewOutcomePath);
            Assert.Contains("accepted", vm.BuilderConversationReviewStateSummary, StringComparison.OrdinalIgnoreCase);

            await vm.OpenBuilderConversationExecutionSessionCommand.ExecuteAsync();
            await vm.OpenBuilderPatchReviewCommand.ExecuteAsync();
            await vm.OpenBuilderPatchReviewOutcomeCommand.ExecuteAsync();
            await vm.CopyBuilderConversationSessionSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderPatchReviewPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderPatchReviewOutcomePathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("route", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_conversation_execution_surface_routes_revision_requests_into_review_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoKnowledgeFiles(repoRoot);
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: new RecordingWorkspaceShellService(),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            vm.BuilderConversationRequestText = "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.";
            await vm.PreviewBuilderConversationCommand.ExecuteAsync();
            await vm.AcceptBuilderConversationCommand.ExecuteAsync();
            await vm.ExecuteBuilderConversationSessionCommand.ExecuteAsync();
            await vm.RequestBuilderConversationRevisionCommand.ExecuteAsync();

            Assert.Contains("revision", vm.BuilderConversationReviewStateSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rejected", vm.BuilderConversationExecutionSessionSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_patch_diff_review_surface_shows_file_approval_and_finalize_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedBuilderRepoKnowledgeFiles(repoRoot);
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            vm.BuilderConversationRequestText = "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.";
            await vm.PreviewBuilderConversationCommand.ExecuteAsync();
            await vm.AcceptBuilderConversationCommand.ExecuteAsync();
            await vm.ExecuteBuilderConversationSessionCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderPatchDiffReviewSummary);
            Assert.True(vm.HasBuilderPatchApplySummary);
            Assert.True(vm.HasBuilderPatchDiffReviewPath);
            Assert.NotEmpty(vm.BuilderPatchDiffFiles);

            vm.SelectedBuilderPatchDiffFilePath = vm.BuilderPatchDiffFiles[0].RelativePath;
            await vm.ApproveSelectedBuilderPatchFileCommand.ExecuteAsync();
            await vm.FinalizeBuilderConversationPatchCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderPatchApplyDecisionPath);
            Assert.Contains("applied", vm.BuilderPatchApplySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("approved", vm.BuilderSelectedPatchDiffStateSummary, StringComparison.OrdinalIgnoreCase);

            await vm.OpenBuilderPatchDiffReviewCommand.ExecuteAsync();
            await vm.CopyBuilderPatchDiffReviewSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("review", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_patch_diff_review_surface_blocks_finalize_when_rejected_file_exists()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(repoRoot);
            var builderService = CreateBuilderExecutionService();

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: new RecordingWorkspaceShellService(),
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal(2, vm.BuilderPatchDiffFiles.Count);

            vm.SelectedBuilderPatchDiffFilePath = Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml");
            await vm.ApproveSelectedBuilderPatchFileCommand.ExecuteAsync();
            vm.SelectedBuilderPatchDiffFilePath = Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs");
            await vm.RejectSelectedBuilderPatchFileCommand.ExecuteAsync();
            await vm.FinalizeBuilderConversationPatchCommand.ExecuteAsync();

            Assert.Contains("blocked", vm.BuilderPatchApplySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rejected", vm.BuilderSelectedPatchDiffStateSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.HasBuilderConversationPatchReviewOutcomePath);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_patch_packaging_surface_shows_snapshot_commit_and_export_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(repoRoot);
            var builderService = CreateBuilderExecutionService();
            var shell = new RecordingWorkspaceShellService();

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            await vm.ApproveAllBuilderPatchFilesCommand.ExecuteAsync();
            await vm.FinalizeBuilderConversationPatchCommand.ExecuteAsync();
            await vm.PrepareBuilderCommitCommand.ExecuteAsync();
            await vm.ExportBuilderPatchBundleCommand.ExecuteAsync();

            Assert.True(vm.HasBuilderPatchSnapshotSummary);
            Assert.True(vm.HasBuilderCommitProposalSummary);
            Assert.True(vm.HasBuilderPatchExportSummary);
            Assert.True(vm.HasBuilderPatchSnapshotPath);
            Assert.True(vm.HasBuilderCommitProposalPath);
            Assert.True(vm.HasBuilderPatchExportPath);
            Assert.True(vm.HasBuilderPatchBundlePath);
            Assert.Equal(2, vm.BuilderPatchSnapshotFiles.Count);
            Assert.Contains("snapshot", vm.BuilderPatchSnapshotSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Shoots Builder Accepted Patch", vm.BuilderCommitProposalMessage, StringComparison.Ordinal);

            await vm.OpenBuilderPatchSnapshotCommand.ExecuteAsync();
            await vm.CopyBuilderCommitMessageCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderPatchSnapshotPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("Route: split_first_low_floor_route", shell.CopiedTexts[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_output_handoff_surface_shows_manual_apply_and_git_block_state()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(repoRoot);
            var builderService = CreateBuilderExecutionService(
                gitReadinessProbe: new ScriptedBuilderGitReadinessProbe(
                    new BuilderGitReadinessObservation(
                        true,
                        "feature/dirty-tree",
                        true,
                        false,
                        "unknown",
                        "blocked_git_dirty_tree",
                        new[] { "Git working tree is dirty and should be reviewed before using the commit handoff." },
                        DateTimeOffset.Parse("2026-03-14T09:30:00+00:00", CultureInfo.InvariantCulture))));
            var shell = new RecordingWorkspaceShellService();

            builderService.ApproveAllBuilderPatchFiles(repoRoot);
            builderService.FinalizeBuilderApprovedPatch(repoRoot);
            builderService.PrepareBuilderOutputHandoff(repoRoot);

            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderOutputHandoffSummary);
            Assert.True(vm.HasBuilderManualApplySummary);
            Assert.True(vm.HasBuilderGitHandoffReadinessSummary);
            Assert.True(vm.HasBuilderGitCommitHandoffSummary);
            Assert.True(vm.HasBuilderOutputHandoffPath);
            Assert.True(vm.HasBuilderManualApplyGuidancePath);
            Assert.True(vm.HasBuilderGitHandoffReadinessPath);
            Assert.True(vm.HasBuilderGitCommitHandoffPath);
            Assert.Contains("manual apply", vm.BuilderOutputHandoffSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dirty", vm.BuilderGitHandoffReadinessSummary, StringComparison.OrdinalIgnoreCase);

            await vm.OpenBuilderOutputHandoffCommand.ExecuteAsync();
            await vm.OpenBuilderManualApplyGuidanceCommand.ExecuteAsync();
            await vm.OpenBuilderGitHandoffReadinessCommand.ExecuteAsync();
            await vm.OpenBuilderGitCommitHandoffCommand.ExecuteAsync();
            await vm.CopyBuilderManualApplyStepsCommand.ExecuteAsync();
            await vm.CopyBuilderOutputHandoffSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderOutputHandoffPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderManualApplyGuidancePathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderGitHandoffReadinessPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderGitCommitHandoffPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(shell.CopiedTexts, text => text.Contains("Inspect the approved patch bundle", StringComparison.Ordinal));
            Assert.Contains(shell.CopiedTexts, text => text.Contains("Git handoff is blocked", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_readiness_gate_surface_shows_confirmed_route_and_opens_expected_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var latestRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.Equal("Confirmed for bounded use", vm.BuilderReadinessGateBadge);
            Assert.True(vm.HasBuilderReadinessGateSummary);
            Assert.True(vm.HasBuilderReadinessCountsSummary);
            Assert.True(vm.HasBuilderReadinessBoundedUseSummary);
            Assert.True(vm.HasBuilderReadinessGatePath);
            Assert.True(vm.HasBuilderReadinessGateHistoryPath);
            Assert.True(vm.HasBuilderRouteStabilitySummaryPath);
            Assert.True(vm.HasBuilderConfirmedClassesSummary);
            Assert.True(vm.HasBuilderConfirmedClassesPath);
            Assert.True(vm.HasBuilderDefaultRouteDecisionSummary);
            Assert.True(vm.HasBuilderDefaultRouteDecisionPath);
            Assert.True(vm.HasBuilderRouteSourceSummary);
            Assert.True(vm.HasBuilderOverrideAvailabilitySummary);
            Assert.False(vm.HasBuilderReadinessLatestContradictionNote);
            Assert.Contains("builder-ready for bounded use", vm.BuilderReadinessBoundedUseSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("defaulted_by_confirmed_policy", vm.BuilderRouteSourceSummary, StringComparison.Ordinal);
            Assert.Contains("override is available", vm.BuilderOverrideAvailabilitySummary, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderReadinessGateCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderReadinessHistoryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderConfirmedClassesCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderDefaultRouteDecisionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRouteStabilitySummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderReadinessSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderConfirmedClassesSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderDefaultRouteDecisionSummaryCommand.CanExecute(null));
            Assert.False(vm.CopyBuilderReadinessContradictionNoteCommand.CanExecute(null));

            await vm.OpenBuilderReadinessGateCommand.ExecuteAsync();
            await vm.OpenBuilderReadinessHistoryCommand.ExecuteAsync();
            await vm.OpenBuilderConfirmedClassesCommand.ExecuteAsync();
            await vm.OpenBuilderDefaultRouteDecisionCommand.ExecuteAsync();
            await vm.OpenBuilderRouteStabilitySummaryCommand.ExecuteAsync();
            await vm.CopyBuilderReadinessSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderConfirmedClassesSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderDefaultRouteDecisionSummaryCommand.ExecuteAsync();

            Assert.Equal(5, shell.OpenedPaths.Count);
            Assert.Equal(BuilderExecutionService.BuilderReadinessGatePath(latestRun.RunFolder), shell.OpenedPaths[0]);
            Assert.Equal(BuilderExecutionService.BuilderReadinessGateHistoryPathForRepo(repoRoot), shell.OpenedPaths[1]);
            Assert.Equal(BuilderExecutionService.BuilderConfirmedTaskClassesPath(latestRun.RunFolder), shell.OpenedPaths[2]);
            Assert.Equal(BuilderExecutionService.BuilderDefaultRouteDecisionPath(latestRun.RunFolder), shell.OpenedPaths[3]);
            Assert.Equal(BuilderExecutionService.BuilderRouteStabilitySummaryPath(latestRun.RunFolder), shell.OpenedPaths[4]);
            Assert.Equal(3, shell.CopiedTexts.Count);
            Assert.Contains("confirmed_for_bounded_use", shell.CopiedTexts[0], StringComparison.Ordinal);
            Assert.Contains("task class(es)", shell.CopiedTexts[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("defaulted_by_confirmed_policy", shell.CopiedTexts[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_contradiction_surface_shows_suspension_and_routes_helpers_to_artifacts()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var contradictedRun = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            await builderService.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

            var result = BuilderExecutionService.LoadBuilderExecutionResult(contradictedRun.RunFolder);
            Assert.NotNull(result);
            var contradicted = result! with
            {
                FinalRouteOutcomeClassification = "launched_and_failed_followup_created",
                PreparedRouteComparisonState = "insufficient_for_scope",
                Summary = "Prepared builder route failed and returned to follow-up."
            };
            File.WriteAllText(
                BuilderExecutionService.BuilderExecutionResultPath(contradictedRun.RunFolder),
                JsonSerializer.Serialize(contradicted, new JsonSerializerOptions { WriteIndented = true }));
            var refreshMethod = typeof(BuilderExecutionService).GetMethod(
                "RefreshBuilderDefaultPolicyArtifacts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(refreshMethod);
            refreshMethod!.Invoke(builderService, new object[] { repoRoot, contradictedRun.RunFolder });

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderReadinessLatestContradictionNote);
            Assert.True(vm.HasBuilderReadinessContradictionsSummary);
            Assert.True(vm.HasBuilderReadinessContradictionsPath);
            Assert.True(vm.HasBuilderDefaultSuspensionSummary);
            Assert.Contains("temporarily suspended", vm.BuilderDefaultSuspensionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("suggested", vm.BuilderDefaultRouteDecisionSummary, StringComparison.Ordinal);

            Assert.True(vm.OpenBuilderReadinessContradictionsCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderReadinessContradictionNoteCommand.CanExecute(null));

            await vm.OpenBuilderReadinessContradictionsCommand.ExecuteAsync();
            await vm.CopyBuilderReadinessContradictionNoteCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderReadinessContradictionsPath(contradictedRun.RunFolder), shell.OpenedPaths);
            Assert.Single(shell.CopiedTexts);
            Assert.Contains("insufficient for scope", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_proof_open_helpers_route_expected_paths()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await builderService.RunBuilderProofMatrixAsync(repoRoot, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await builderService.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.OpenBuilderProofSummaryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderProofRunFolderCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderModelFloorVerdictCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderFailurePatternsCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderExternalProofSummaryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderModelFloorGuidanceCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderTrustBandsCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderScopeSummaryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRoutingRecommendationCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderEscalationDecisionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRoutingPlanCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderStrongerTierAvailabilityCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderComparativeProofSummaryCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderRoutingPolicyEvidenceCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderSplitFirstPlanCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderTieredRoutingPolicyCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderProofSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderScopeSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderRoutingRecommendationCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderSplitTaskGuidanceCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderComparativeProofSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderRoutingPolicySummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderSplitFirstPlanSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderPrimaryRoutingRecommendationCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderWeakSpotMitigationSummaryCommand.CanExecute(null));

            await vm.OpenBuilderProofSummaryCommand.ExecuteAsync();
            await vm.OpenBuilderProofRunFolderCommand.ExecuteAsync();
            await vm.OpenBuilderModelFloorVerdictCommand.ExecuteAsync();
            await vm.OpenBuilderFailurePatternsCommand.ExecuteAsync();
            await vm.OpenBuilderExternalProofSummaryCommand.ExecuteAsync();
            await vm.OpenBuilderModelFloorGuidanceCommand.ExecuteAsync();
            await vm.OpenBuilderTrustBandsCommand.ExecuteAsync();
            await vm.OpenBuilderScopeSummaryCommand.ExecuteAsync();
            await vm.OpenBuilderRoutingRecommendationCommand.ExecuteAsync();
            await vm.OpenBuilderEscalationDecisionCommand.ExecuteAsync();
            await vm.OpenBuilderRoutingPlanCommand.ExecuteAsync();
            await vm.OpenBuilderStrongerTierAvailabilityCommand.ExecuteAsync();
            await vm.OpenBuilderComparativeProofSummaryCommand.ExecuteAsync();
            await vm.OpenBuilderRoutingPolicyEvidenceCommand.ExecuteAsync();
            await vm.OpenBuilderSplitFirstPlanCommand.ExecuteAsync();
            await vm.OpenBuilderTieredRoutingPolicyCommand.ExecuteAsync();
            await vm.CopyBuilderProofSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderScopeSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderRoutingRecommendationCommand.ExecuteAsync();
            await vm.CopyBuilderSplitTaskGuidanceCommand.ExecuteAsync();
            await vm.CopyBuilderComparativeProofSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderRoutingPolicySummaryCommand.ExecuteAsync();
            await vm.CopyBuilderSplitFirstPlanSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderPrimaryRoutingRecommendationCommand.ExecuteAsync();
            await vm.CopyBuilderWeakSpotMitigationSummaryCommand.ExecuteAsync();

            Assert.Equal(16, shell.OpenedPaths.Count);
            Assert.Equal(run.SummaryArtifactPath, shell.OpenedPaths[0]);
            Assert.Equal(run.RunFolder, shell.OpenedPaths[1]);
            Assert.Equal(run.VerdictArtifactPath, shell.OpenedPaths[2]);
            Assert.Equal(BuilderExecutionService.BuilderModelFloorFailurePatternsPath(run.RunFolder), shell.OpenedPaths[3]);
            Assert.Equal(BuilderExecutionService.BuilderExternalProofSummaryPath(run.RunFolder), shell.OpenedPaths[4]);
            Assert.Equal(BuilderExecutionService.BuilderModelFloorPolicySummaryPath(run.RunFolder), shell.OpenedPaths[5]);
            Assert.Equal(BuilderExecutionService.BuilderModelTrustBandsPath(run.RunFolder), shell.OpenedPaths[6]);
            Assert.Equal(BuilderExecutionService.BuilderModelScopeSummaryPath(run.RunFolder), shell.OpenedPaths[7]);
            Assert.Equal(BuilderExecutionService.BuilderModelRoutingRecommendationPath(run.RunFolder), shell.OpenedPaths[8]);
            Assert.Equal(BuilderExecutionService.BuilderModelEscalationDecisionPath(run.RunFolder), shell.OpenedPaths[9]);
            Assert.Equal(BuilderExecutionService.BuilderModelRoutingPlanPath(run.RunFolder), shell.OpenedPaths[10]);
            Assert.Equal(BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder), shell.OpenedPaths[11]);
            Assert.Equal(BuilderExecutionService.BuilderComparativeProofSummaryPath(run.RunFolder), shell.OpenedPaths[12]);
            Assert.Equal(BuilderExecutionService.BuilderRoutingPolicyEvidencePath(run.RunFolder), shell.OpenedPaths[13]);
            Assert.Equal(BuilderExecutionService.BuilderSplitFirstPlanPath(run.RunFolder), shell.OpenedPaths[14]);
            Assert.Equal(BuilderExecutionService.BuilderTieredRoutingPolicyPath(run.RunFolder), shell.OpenedPaths[15]);
            Assert.Equal(9, shell.CopiedTexts.Count);
            Assert.Contains("# Builder Proof Summary", shell.CopiedTexts[0], StringComparison.Ordinal);
            Assert.Contains("Clean band=", shell.CopiedTexts[1], StringComparison.Ordinal);
            Assert.Contains("out of scope", shell.CopiedTexts[2], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Reduce the touched file count", shell.CopiedTexts[3], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("# Builder Comparative Proof Summary", shell.CopiedTexts[4], StringComparison.Ordinal);
            Assert.Contains("Split first, keep low-floor", shell.CopiedTexts[5], StringComparison.Ordinal);
            Assert.Contains("Split first", shell.CopiedTexts[6], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Primary route:", shell.CopiedTexts[7], StringComparison.Ordinal);
            Assert.Contains("file_placement_mistake", shell.CopiedTexts[8], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_model_routing_surface_shows_policy_decision_and_helpers()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                repoRoot,
                builderService,
                "bounded_refactor",
                "split_first_low_floor",
                "bounded-refactor",
                "Bounded refactor");
            builderService.PreviewBuilderConversationIntake(
                repoRoot,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderModelCapabilityMatrixSummary);
            Assert.True(vm.HasBuilderModelRoutingRulesSummary);
            Assert.True(vm.HasBuilderModelRoutingStabilitySummary);
            Assert.True(vm.HasBuilderCurrentModelDecisionSummary);
            Assert.True(vm.HasBuilderModelEscalationDecisionSummaryText);
            Assert.Contains("split-first", vm.BuilderModelCapabilityMatrixSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Low-floor split-first", vm.BuilderModelRoutingRulesSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("low_floor_model_tier", vm.BuilderCurrentModelDecisionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("low_floor_via_split_first", vm.BuilderModelEscalationDecisionSummaryText, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderModelCapabilityMatrixCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderModelRoutingRulesCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderCurrentModelDecisionCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderModelEscalationDecisionArtifactCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderModelRoutingRulesSummaryCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderCurrentModelDecisionSummaryCommand.CanExecute(null));

            await vm.OpenBuilderModelCapabilityMatrixCommand.ExecuteAsync();
            await vm.OpenBuilderModelRoutingRulesCommand.ExecuteAsync();
            await vm.OpenBuilderCurrentModelDecisionCommand.ExecuteAsync();
            await vm.OpenBuilderModelEscalationDecisionArtifactCommand.ExecuteAsync();
            await vm.CopyBuilderModelRoutingRulesSummaryCommand.ExecuteAsync();
            await vm.CopyBuilderCurrentModelDecisionSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderModelCapabilityMatrixPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderModelRoutingPolicyPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderModelDecisionPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("Low-floor split-first", shell.CopiedTexts[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("low_floor_model_tier", shell.CopiedTexts[1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_diagnostic_surface_shows_route_model_and_failure_explanations()
    {
        var repoRoot = CreateValidationRepoRoot();

        try
        {
            var builderService = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                repoRoot,
                builderService,
                "bounded_refactor",
                "stronger_tier_required",
                "bounded-refactor",
                "Bounded refactor",
                strongerTierAvailabilityState: "unavailable");
            builderService.PreviewBuilderConversationIntake(
                repoRoot,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            var shell = new RecordingWorkspaceShellService();
            var vm = BuildViewModel(
                new FixedBackendProbeService(
                    new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null),
                    new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null)),
                new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")),
                workspaceShell: shell,
                validationRunnerService: new ValidationRunnerService(repoRoot),
                validationSettingsStore: new InMemoryValidationSettingsStore(),
                builderExecutionService: builderService);

            Assert.True(vm.HasBuilderRouteExplanationSummary);
            Assert.True(vm.HasBuilderModelDecisionExplanationSummary);
            Assert.True(vm.HasBuilderFailureAnalysisSummary);
            Assert.True(vm.HasBuilderOperatorDiagnosticSummary);
            Assert.Contains("task_out_of_scope_route", vm.BuilderRouteExplanationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stronger_builder_tier", vm.BuilderModelDecisionExplanationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("launch_blocked_model_policy", vm.BuilderFailureAnalysisSummary, StringComparison.OrdinalIgnoreCase);

            Assert.True(vm.OpenBuilderRouteExplanationCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderModelDecisionExplanationCommand.CanExecute(null));
            Assert.True(vm.OpenBuilderFailureAnalysisCommand.CanExecute(null));
            Assert.True(vm.CopyBuilderDiagnosticSummaryCommand.CanExecute(null));

            await vm.OpenBuilderRouteExplanationCommand.ExecuteAsync();
            await vm.OpenBuilderModelDecisionExplanationCommand.ExecuteAsync();
            await vm.OpenBuilderFailureAnalysisCommand.ExecuteAsync();
            await vm.CopyBuilderDiagnosticSummaryCommand.ExecuteAsync();

            Assert.Contains(BuilderExecutionService.BuilderRouteExplanationPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderModelDecisionExplanationPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains(BuilderExecutionService.BuilderFailureAnalysisPathForRepo(repoRoot), shell.OpenedPaths);
            Assert.Contains("Builder Operator Diagnostic Summary", shell.CopiedTexts[0], StringComparison.Ordinal);
            Assert.Contains("Final execution outcome", shell.CopiedTexts[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "Shoots.sln")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException("Could not locate Shoots.sln from test base directory.");
    }

    private static async Task<BuilderProofRun> PrepareSyntheticBuilderModelRoutingProofAsync(
        string root,
        BuilderExecutionService service,
        string taskClass,
        string policyState,
        string targetId,
        string targetLabel,
        string strongerTierAvailabilityState = "available")
    {
        SeedBuilderRepoKnowledgeFiles(root);
        var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
        WriteSyntheticBuilderDefaultPolicy(root, run, taskClass, policyState, targetId, targetLabel);
        WriteSyntheticBuilderStrongerTierAvailability(run, strongerTierAvailabilityState);
        service.RefreshBuilderModelRoutingArtifacts(root);
        return run;
    }

    private static void WriteSyntheticBuilderDefaultPolicy(
        string root,
        BuilderProofRun run,
        string taskClass,
        string policyState,
        string targetId,
        string targetLabel)
    {
        var observedUtc = DateTimeOffset.Parse("2026-03-14T18:30:00+00:00", CultureInfo.InvariantCulture);
        var complexity = new BuilderProofComplexityDimensions(
            FileCountTouched: 2,
            ProjectCountTouched: 1,
            DependencyReferenceChangeCount: 0,
            TestChangesRequired: string.Equals(taskClass, "test_extension", StringComparison.Ordinal),
            NewFileCreationCount: 0,
            PromptAmbiguity: "low");
        var evidencePaths = new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) };
        var entry = new BuilderDefaultPolicyTaskClassEntry(
            "wpf_app",
            targetId,
            targetLabel,
            taskClass,
            complexity,
            policyState,
            string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal) ? "partial_implementation_gap" : string.Empty,
            $"Synthetic default policy for {taskClass} set to {policyState}.",
            new[] { $"Synthetic model routing evidence for {taskClass}={policyState}." },
            evidencePaths);
        var policy = new BuilderDefaultPolicy(
            run.ProofRunId,
            BuilderExecutionService.BuilderProofFloorModelId,
            string.Equals(policyState, "direct_low_floor", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "split_first_low_floor", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "low_floor_with_repair_loop_expected", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_optional", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_recommended", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            new[] { entry },
            evidencePaths,
            $"Synthetic default policy keeps {taskClass} at {policyState}.",
            BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder),
            observedUtc);
        File.WriteAllText(
            BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder),
            JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));

        var history = new BuilderDefaultPolicyHistory(
            20,
            new[]
            {
                new BuilderDefaultPolicyHistoryEntry(
                    policy.SourceProofRunId,
                    policy.CurrentModelId,
                    policy.Summary,
                    policy.ArtifactPath,
                    policy.ObservedUtc,
                    policy.TaskClassEntries)
            });
        File.WriteAllText(
            BuilderExecutionService.BuilderDefaultPolicyHistoryPathForRepo(root),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSyntheticBuilderStrongerTierAvailability(BuilderProofRun run, string availabilityState)
    {
        var available = string.Equals(availabilityState, "available", StringComparison.Ordinal);
        var availability = new BuilderStrongerTierAvailability(
            BuilderExecutionService.BuilderProofFloorModelId,
            "stronger_builder_tier",
            "qwen2.5:7b-instruct",
            available ? "qwen2.5:7b-instruct" : string.Empty,
            availabilityState,
            available
                ? "qwen2.5:7b-instruct is available for bounded comparative proof."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            "ollama",
            "http://localhost:11434",
            string.Empty,
            available
                ? new[] { BuilderExecutionService.BuilderProofFloorModelId, "qwen2.5:7b-instruct" }
                : new[] { BuilderExecutionService.BuilderProofFloorModelId },
            available
                ? "Resolved qwen2.5:7b-instruct from the bounded stronger-tier candidate set."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            Array.Empty<string>(),
            available
                ? "qwen2.5:7b-instruct is available for bounded comparative proof."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder),
            DateTimeOffset.Parse("2026-03-14T18:31:00+00:00", CultureInfo.InvariantCulture));
        File.WriteAllText(
            BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder),
            JsonSerializer.Serialize(availability, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static BuilderExecutionService CreateBuilderExecutionService(
        IBuilderProofCommandRunner? runner = null,
        IBuilderStrongerTierResolver? resolver = null,
        IBuilderToolchainCapabilityScanner? capabilityScanner = null,
        IBuilderGitReadinessProbe? gitReadinessProbe = null)
    {
        var registry = new ToolRegistry("etc/ui.tools.catalog.json");
        var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
        return new BuilderExecutionService(
            runtimeBridge,
            new ArtifactManager(),
            registry,
            runner,
            resolver ?? new AvailableBuilderStrongerTierResolver(),
            capabilityScanner ?? CreateDefaultBuilderToolchainCapabilityScanner(),
            gitReadinessProbe ?? new ScriptedBuilderGitReadinessProbe(
                new BuilderGitReadinessObservation(
                    false,
                    string.Empty,
                    false,
                    false,
                    "unknown",
                    "blocked_git_missing_repo",
                    new[] { "No Git repository was detected for the approved patch handoff." },
                    DateTimeOffset.Parse("2026-03-14T08:30:00+00:00", CultureInfo.InvariantCulture))));
    }

    private static IBuilderToolchainCapabilityScanner CreateDefaultBuilderToolchainCapabilityScanner()
    {
        var observedUtc = DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", CultureInfo.InvariantCulture);
        return new ScriptedBuilderToolchainCapabilityScanner(
            new BuilderToolchainCapabilityObservation(
                "dotnet",
                "sdk",
                @"C:\tools\dotnet\dotnet.exe",
                "8.0.204",
                true,
                true,
                "probe_succeeded",
                string.Empty,
                observedUtc),
            new BuilderToolchainCapabilityObservation(
                "msbuild",
                "build_tool",
                @"C:\tools\msbuild\MSBuild.exe",
                "17.10.1",
                true,
                true,
                "probe_succeeded",
                string.Empty,
                observedUtc));
    }

    private static MainWindowViewModel BuildViewModel(
        IBackendProbeService probeService,
        IOllamaClient ollamaClient,
        bool includeProfile = true,
        IWorkspaceShellService? workspaceShell = null,
        Shoots.UI.Builder.IPlanner? planner = null,
        IValidationSettingsStore? validationSettingsStore = null,
        IValidationRunnerService? validationRunnerService = null,
        IRepairAttemptService? repairAttemptService = null,
        ISemanticReuseService? semanticReuseService = null,
        BuilderExecutionService? builderExecutionService = null)
    {
        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new DeterministicEnvironmentProfileService(includeProfile),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            new DeterministicWorkspaceProvider(),
            workspaceShell ?? new NullWorkspaceShellService(),
            new InMemoryDatabaseIntentStore(),
            new ToolTierPrompt(),
            new SystemBlueprintStore(),
            new ExecutionEnvironmentSettingsStore(),
            new InMemoryAiPolicyStore(),
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            probeService,
            ollamaClient,
            validationSettingsStore: validationSettingsStore,
            validationRunnerService: validationRunnerService,
            repairAttemptService: repairAttemptService,
            planner: planner,
            builderExecutionService: builderExecutionService,
            autoRefreshBackends: false,
            semanticReuseService: semanticReuseService);
    }

    private static string CreateValidationRepoRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-validation-vm-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        return root;
    }

    private static void SeedRepairReviewArtifacts(string runPath, string repoRoot, string repairId, string improvementState)
    {
        var repairFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", repairId);
        var validationFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", $"validation-{repairId}");
        Directory.CreateDirectory(repairFolder);
        Directory.CreateDirectory(validationFolder);
        var bundlePath = Path.Combine(repairFolder, "repair_bundle.json");
        File.WriteAllText(bundlePath, "{}");

        var comparison = new RepairComparisonRecord(
            repairId,
            "source-run-001",
            "failed",
            "Validation failed: Tests failed.",
            "Running UI tests",
            "Tests failed.",
            $"validation-{repairId}",
            string.Equals(improvementState, "passed", System.StringComparison.Ordinal) ? "passed" : "failed",
            string.Equals(improvementState, "passed", System.StringComparison.Ordinal)
                ? "Validation passed (1 stage)."
                : "Validation failed: Smoke failed.",
            string.Equals(improvementState, "passed", System.StringComparison.Ordinal) ? "Completed" : "Running smoke validation",
            string.Equals(improvementState, "passed", System.StringComparison.Ordinal) ? string.Empty : "Smoke failed.",
            improvementState,
            new[] { Path.Combine(repoRoot, "src", "Generated.cs") },
            "Repair applied deterministic changes.",
            bundlePath,
            repairFolder,
            validationFolder,
            System.DateTimeOffset.UtcNow);
        RepairReviewArtifactsService.SaveComparison(comparison);

        RepairReviewArtifactsService.AppendHistory(
            runPath,
            new RepairHistoryEntry(
                repairId,
                comparison.RecordedUtc,
                comparison.SourceValidationRunId,
                comparison.RepairedValidationRunId,
                "changed",
                comparison.ImprovementState,
                comparison.RepairSummary,
                bundlePath,
                repairFolder,
                validationFolder,
                RepairReviewArtifactsService.ComparisonPathForRepair(repairFolder)),
            keepLast: 5);
    }

    private static void SeedPromotedRepairArtifacts(string runPath, string repoRoot, string repairId, string improvementState)
    {
        SeedRepairReviewArtifacts(runPath, repoRoot, repairId, improvementState);

        var repairFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", repairId);
        var comparison = RepairReviewArtifactsService.LoadComparison(RepairReviewArtifactsService.ComparisonPathForRepair(repairFolder));
        var history = RepairReviewArtifactsService.LoadHistory(runPath).Attempts.First();
        Assert.NotNull(comparison);

        var promotion = RepairReviewArtifactsService.CreatePromotion(
            "run-001",
            runPath,
            history,
            comparison!,
            $"Repair outcome {improvementState}.",
            string.Empty,
            System.DateTimeOffset.UtcNow);
        promotion = RepairReviewArtifactsService.WriteAuditSummary(comparison!, promotion);
        RepairReviewArtifactsService.SavePromotion(runPath, promotion);
    }

    private static ValidationRunResult SuccessfulValidationResult(string repoRoot)
    {
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-success");
        Directory.CreateDirectory(outputFolder);
        return new ValidationRunResult(
            "run-success",
            "Run full validation loop",
            outputFolder,
            true,
            "Validation passed (1 stage).",
            null,
            null,
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            System.DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("build_ui", "Building UI", "passed", "Build succeeded.", Path.Combine(outputFolder, "01-build-ui.log"), 0, 25)
            });
    }

    private static void SeedBuilderRepoLanguagePolicyFiles(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui.Tests"));
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Shoots.Ui.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void SeedBuilderRepoKnowledgeFiles(string root)
    {
        SeedBuilderRepoLanguagePolicyFiles(root);

        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shoots.Ui\Shoots.Ui.csproj" />
              </ItemGroup>
            </Project>
            """);

        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "Builder"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "ViewModels"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "Services"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests"));

        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "MainWindow.xaml"),
            """
            <Window x:Class="Shoots.UI.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
            """);
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
            "namespace Shoots.UI.ViewModels; public sealed class MainWindowViewModel { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Builder", "BuilderExecutionService.cs"),
            "namespace Shoots.UI.Builder; public sealed class BuilderExecutionService { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Services", "ValidationRunnerService.cs"),
            "namespace Shoots.UI.Services; public sealed class ValidationRunnerService { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "MainWindowViewModelBackendStatusTests.cs"),
            "namespace Shoots.UI.Tests; public sealed class MainWindowViewModelBackendStatusTests { }");

        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core", "Shoots.Runtime.Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core", "RuntimeLoop.cs"),
            "namespace Shoots.Runtime.Core; public sealed class RuntimeLoop { }");
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests", "Shoots.Runtime.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shoots.Runtime.Core\Shoots.Runtime.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests", "RuntimeLoopTests.cs"),
            "namespace Shoots.Runtime.Tests; public sealed class RuntimeLoopTests { }");
    }

    private static void SeedSyntheticBuilderPatchDiffReviewArtifacts(string root)
    {
        SeedBuilderRepoKnowledgeFiles(root);
        Directory.CreateDirectory(BuilderExecutionService.BuilderProofRootForRepo(root));

        var now = DateTimeOffset.Parse("2026-03-14T03:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var intake = new BuilderConversationIntake(
            "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.",
            "bounded_refactor",
            "wpf_desktop_dotnet",
            "WPF/Desktop .NET",
            "strong_match",
            "Repo retrieval matched the WPF UI surfaces strongly.",
            "route_allowed",
            "Capability review allows the preferred WPF/Desktop .NET stack.",
            "split_first_low_floor_route",
            "default_route_policy",
            true,
            "optional",
            "accept_suggested_route",
            "ready_for_launch",
            string.Empty,
            Array.Empty<string>(),
            "Conversation intake is ready for launch.",
            BuilderExecutionService.BuilderConversationIntakePathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationIntakePathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(intake, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var handoff = new BuilderConversationHandoff(
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            intake.RetrievalConfidenceState,
            intake.CapabilityRoutingState,
            intake.SelectedRoute,
            intake.RouteSourceState,
            intake.OperatorDecisionState,
            intake.LaunchReadinessState,
            intake.BlockReason,
            new[] { intake.ArtifactPath },
            "Conversation handoff is ready for execution.",
            BuilderExecutionService.BuilderConversationHandoffPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationHandoffPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(handoff, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var changedFiles = new[]
        {
            new BuilderPatchReviewChangedFile(
                Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"),
                "ui_markup",
                "modified",
                "MainWindow.xaml was modified to satisfy the bounded ui markup route.",
                true),
            new BuilderPatchReviewChangedFile(
                Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
                "view_model",
                "modified",
                "MainWindowViewModel.cs was modified to satisfy the bounded view model route.",
                true)
        };

        var session = new BuilderConversationExecutionSession(
            "session-1",
            "intake-1",
            "handoff-1",
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            intake.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            intake.CapabilitySummary,
            "awaiting_patch_review",
            "awaiting_operator_review",
            "Awaiting operator review",
            "pending_operator_review",
            "Build=passed. Test=passed. Outcome=launched_and_passed.",
            string.Empty,
            string.Empty,
            string.Empty,
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            string.Empty,
            changedFiles,
            new[]
            {
                new BuilderConversationExecutionStage(
                    "awaiting_operator_review",
                    "Awaiting operator review",
                    "active",
                    "Candidate changes are ready for operator review.",
                    Array.Empty<string>())
            },
            new[] { BuilderExecutionService.BuilderConversationHandoffPathForRepo(root) },
            "Execution session is awaiting patch review.",
            BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var patchReview = new BuilderPatchReview(
            session.SessionId,
            intake.ArtifactPath,
            handoff.ArtifactPath,
            intake.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            session.ValidationSummary,
            "ready_for_operator_review",
            changedFiles,
            new[] { session.ArtifactPath, handoff.ArtifactPath },
            "Patch review found 2 changed file candidate(s) on route split_first_low_floor_route.",
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(patchReview, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var patchDiffReview = new BuilderPatchDiffReview(
            session.SessionId,
            patchReview.SessionId,
            patchReview.ArtifactPath,
            "all_files_pending",
            "ready_for_operator_review",
            new[]
            {
                new BuilderPatchDiffReviewFileEntry(
                    Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"),
                    "ui_markup",
                    "modified",
                    "Diff preview shows UI copy and layout changes.",
                    "@@ MainWindow.xaml\n-<TextBlock Text=\"Old\" />\n+<TextBlock Text=\"New\" />",
                    "pending_review",
                    string.Empty,
                    now),
                new BuilderPatchDiffReviewFileEntry(
                    Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
                    "view_model",
                    "modified",
                    "Diff preview shows view-model state changes.",
                    "@@ MainWindowViewModel.cs\n-private string _status = \"old\";\n+private string _status = \"new\";",
                    "pending_review",
                    string.Empty,
                    now)
            },
            new[] { session.ArtifactPath, patchReview.ArtifactPath },
            "Patch diff review is waiting on file-level approval.",
            BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(patchDiffReview, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(ValidationRunnerService Service, ValidationRunResult LatestResult)> SeedValidationHandoffArtifactsAsync(string repoRoot)
    {
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(System.StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        await service.RunAsync(ValidationAction.BuildUiProject, settings);
        await Task.Delay(5);
        var failedResult = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
        return (service, failedResult);
    }

    private static void SeedPlanningPlaybookArtifacts(string repoRoot)
    {
        var projectRoot = Path.Combine(repoRoot, ".state", "projects", "playbook-project");
        var runPath = Path.Combine(projectRoot, "runs", "generated-run-001");
        Directory.CreateDirectory(runPath);
        File.WriteAllText(
            Path.Combine(projectRoot, "project.json"),
            JsonSerializer.Serialize(new
            {
                Id = "generated-run-001",
                Name = "Playbook Project",
                Description = "Playbook evidence",
                ProjectRootPath = projectRoot
            }, new JsonSerializerOptions { WriteIndented = true }));
        GeneratedOutputValidationLinkService.Save(new GeneratedOutputValidationLink(
            "generated-run-001",
            runPath,
            projectRoot,
            "passed",
            "Validation passed cleanly.",
            "Validate generated output",
            "validation-generated-run-001",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-generated-run-001"),
            null,
            System.DateTimeOffset.UtcNow));

        var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true, false, true, true, true, 2, true, 3);
        var service = new SemanticReuseService(repoRoot);
        var index = service.RefreshLocalIndex(settings);
        var document = Assert.Single(index.Entries, entry => entry.CaseType == "generated_output_pattern");
        var reference = new RepairReferenceCase(
            document.DocumentId,
            "planning",
            "Current planning context",
            document.CaseType,
            document.Title,
            document.Outcome,
            "High",
            "exact linked history",
            document.SourceRunId,
            document.PrimaryArtifactPath,
            new[] { document.PrimaryArtifactPath },
            string.Empty);

        SemanticReuseService.RecordSuggestionOutcome(
            repoRoot,
            new[] { reference },
            "planning",
            "generated-run-001",
            "validation-playbook-001",
            string.Empty,
            "passed",
            "Validation passed cleanly.",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-playbook-001", "validation_result.json"),
            new[] { runPath },
            "validation",
            System.DateTimeOffset.UtcNow.AddMinutes(-2),
            settings);
        SemanticReuseService.RecordSuggestionOutcome(
            repoRoot,
            new[] { reference },
            "planning",
            "generated-run-001",
            "validation-playbook-002",
            string.Empty,
            "passed",
            "Validation passed cleanly.",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-playbook-002", "validation_result.json"),
            new[] { runPath },
            "validation",
            System.DateTimeOffset.UtcNow.AddMinutes(-1),
            settings);
        service.RefreshLocalIndex(settings);
    }

    private static void SeedValidationHistoryLedger(string repoRoot, IReadOnlyList<ValidationHistoryEntry> entries)
    {
        var artifactsRoot = ValidationRunnerService.ValidationArtifactsRootForRepo(repoRoot);
        Directory.CreateDirectory(artifactsRoot);
        var ledger = new ValidationHistoryLedger(entries.Count, entries);
        File.WriteAllText(
            ValidationRunnerService.HistoryLedgerPathForRepo(repoRoot),
            JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ValidationHistoryEntry ValidationHistoryEntryForUi(
        string runId,
        string actionLabel,
        int minuteOffset,
        string overallResult,
        string stabilityClassification,
        string firstFailureSummary,
        string firstFailureStage,
        string failingTestName,
        bool retryUsed)
    {
        var startedUtc = System.DateTimeOffset.Parse("2026-03-10T12:00:00+00:00").AddMinutes(minuteOffset);
        var completedUtc = startedUtc.AddSeconds(30);
        var outputFolder = Path.Combine(Path.GetTempPath(), runId);
        var stageLabel = string.IsNullOrWhiteSpace(firstFailureStage) ? "Building UI" : firstFailureStage;
        return new ValidationHistoryEntry(
            runId,
            actionLabel,
            outputFolder,
            Path.Combine(outputFolder, "validation_result.json"),
            Path.Combine(outputFolder, "validation_stability.json"),
            startedUtc,
            completedUtc,
            overallResult,
            stabilityClassification,
            stabilityClassification switch
            {
                "passed_on_retry" => "Passed after retry",
                "flaky_suspected" => "Flaky suspected",
                "failed" => "Failed",
                _ => "Passed cleanly"
            },
            firstFailureSummary,
            firstFailureStage,
            failingTestName,
            retryUsed,
            retryUsed ? 1 : 0,
            new[]
            {
                new ValidationHistoryStageOutcome(
                    "ui_tests",
                    stageLabel,
                    string.Equals(overallResult, "passed", System.StringComparison.Ordinal) ? "passed" : "failed",
                    stabilityClassification,
                    retryUsed)
            });
    }

    private static void WriteValidationResultArtifact(ValidationRunResult result)
    {
        Directory.CreateDirectory(result.OutputFolder);
        File.WriteAllText(
            Path.Combine(result.OutputFolder, "validation_result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }



    private static void CreateRunArtifacts(string runPath)
    {
        Directory.CreateDirectory(runPath);
        File.WriteAllText(Path.Combine(runPath, "verification_report.json"), "{}\n");
        File.WriteAllText(Path.Combine(runPath, "operator_flow.json"), "{}\n");
        File.WriteAllText(Path.Combine(runPath, "transport_equivalence.json"), "{}\n");
    }

    private sealed class DeterministicEnvironmentProfileService : IEnvironmentProfileService
    {
        private static readonly IEnvironmentProfile Profile = new DeterministicEnvironmentProfile();

        public DeterministicEnvironmentProfileService(bool includeProfile)
        {
            Profiles = includeProfile ? new[] { Profile } : System.Array.Empty<IEnvironmentProfile>();
        }

        public IReadOnlyList<IEnvironmentProfile> Profiles { get; }

        public EnvironmentProfileResult? LastResult => null;

        public EnvironmentCapability AvailableCapabilities => EnvironmentCapability.None;

        public EnvironmentProfileResult ApplyProfile(string sandboxRoot, IEnvironmentProfile profile)
        {
            return new EnvironmentProfileResult(profile.Name, System.Array.Empty<string>(), profile.DeclaredCapabilities, System.DateTimeOffset.UtcNow);
        }
    }

    private sealed class InMemoryValidationSettingsStore : IValidationSettingsStore
    {
        public ValidationSettings Current { get; set; } = new(false, false, 5, false, false, false);
        public ValidationSettings? LastSaved { get; private set; }

        public ValidationSettings Load() => Current;

        public void Save(ValidationSettings settings)
        {
            LastSaved = settings;
            Current = settings;
        }
    }

    private sealed class FixedSemanticReuseService : ISemanticReuseService
    {
        private readonly SemanticReuseSuggestionSet _result;

        public FixedSemanticReuseService(string repoRoot, SemanticReuseSuggestionSet result)
        {
            RepoRoot = repoRoot;
            _result = result;
        }

        public string RepoRoot { get; }
        public string DesignNotePath => _result.DesignNotePath;
        public string IndexPath => _result.IndexPath;
        public string LinkagePath => _result.LinkagePath;

        public SemanticReuseIndexLedger RefreshLocalIndex(ValidationSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_result.DesignNotePath)!);
            File.WriteAllText(_result.DesignNotePath, "# note");
            File.WriteAllText(_result.IndexPath, "{}");
            File.WriteAllText(_result.LinkagePath, "{}");
            return new SemanticReuseIndexLedger(settings.SemanticReuseRetentionCount, System.DateTimeOffset.UtcNow, Array.Empty<SemanticReuseIndexedCase>());
        }

        public Task<SemanticReuseSuggestionSet> FindSimilarCasesAsync(IReadOnlyList<SemanticReuseQuery> queries, ValidationSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class DeterministicValidationRunnerService : IValidationRunnerService
    {
        private readonly ValidationRunResult _result;

        public DeterministicValidationRunnerService(string repoRoot, ValidationRunResult result)
        {
            RepoRoot = repoRoot;
            ValidationRunsRoot = Path.Combine(repoRoot, ".codex", "validation-ui", "runs");
            _result = result;
        }

        public string RepoRoot { get; }
        public string ValidationRunsRoot { get; }

        public IReadOnlyList<string> GetStageLabels(ValidationAction action, bool includeValidateBuild)
            => _result.Stages.Select(stage => stage.StageLabel).ToArray();

        public IReadOnlyList<ValidationRunSummary> LoadRecentRuns(int maxCount)
            => new[]
            {
                new ValidationRunSummary(
                    _result.RunId,
                    _result.ActionLabel,
                    _result.OutputFolder,
                    _result.Success,
                    _result.Summary,
                    _result.StartedUtc,
                    _result.CompletedUtc)
            };

        public Task<ValidationRunResult> RunAsync(ValidationAction action, ValidationSettings settings, Action<ValidationProgressEvent>? progress = null, CancellationToken ct = default)
        {
            progress?.Invoke(new ValidationProgressEvent("run_started", string.Empty, _result.ActionLabel, "active", _result.Summary, null, null, _result.OutputFolder, System.DateTimeOffset.UtcNow));
            foreach (var stage in _result.Stages)
            {
                progress?.Invoke(new ValidationProgressEvent("stage_started", stage.StageId, stage.StageLabel, "active", $"{stage.StageLabel} started.", null, stage.LogPath, _result.OutputFolder, System.DateTimeOffset.UtcNow));
                progress?.Invoke(new ValidationProgressEvent("stage_completed", stage.StageId, stage.StageLabel, stage.Status == "failed" ? "failed" : "completed", stage.Summary, null, stage.LogPath, _result.OutputFolder, System.DateTimeOffset.UtcNow));
            }

            progress?.Invoke(new ValidationProgressEvent("run_completed", string.Empty, _result.ActionLabel, _result.Success ? "completed" : "failed", _result.Summary, null, _result.FirstFailureLogPath, _result.OutputFolder, System.DateTimeOffset.UtcNow));
            return Task.FromResult(_result);
        }
    }

    private sealed class BlockingValidationRunnerService : IValidationRunnerService
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _actionLabel;
        private readonly string _stageId;
        private readonly string _stageLabel;

        public BlockingValidationRunnerService(
            string repoRoot,
            ValidationAction action = ValidationAction.RunFullValidationLoop,
            string actionLabel = "Run full validation loop",
            string stageId = "build_ui",
            string stageLabel = "Building UI")
        {
            RepoRoot = repoRoot;
            ValidationRunsRoot = Path.Combine(repoRoot, ".codex", "validation-ui", "runs");
            _actionLabel = actionLabel;
            _stageId = stageId;
            _stageLabel = stageLabel;
        }

        public string RepoRoot { get; }
        public string ValidationRunsRoot { get; }

        public IReadOnlyList<string> GetStageLabels(ValidationAction action, bool includeValidateBuild)
            => new[] { _stageLabel };

        public IReadOnlyList<ValidationRunSummary> LoadRecentRuns(int maxCount)
            => System.Array.Empty<ValidationRunSummary>();

        public async Task<ValidationRunResult> RunAsync(ValidationAction action, ValidationSettings settings, Action<ValidationProgressEvent>? progress = null, CancellationToken ct = default)
        {
            var outputFolder = Path.Combine(ValidationRunsRoot, "run-blocking");
            Directory.CreateDirectory(outputFolder);
            var logPath = Path.Combine(outputFolder, "01-build-ui.log");
            progress?.Invoke(new ValidationProgressEvent("run_started", string.Empty, _actionLabel, "active", "Validation output folder ready.", null, null, outputFolder, System.DateTimeOffset.UtcNow));
            progress?.Invoke(new ValidationProgressEvent("stage_started", _stageId, _stageLabel, "active", $"{_stageLabel} started.", null, logPath, outputFolder, System.DateTimeOffset.UtcNow));
            _started.TrySetResult(true);
            using var _ = ct.Register(() => _release.TrySetCanceled(ct));
            await _release.Task;

            var result = new ValidationRunResult(
                "run-blocking",
                _actionLabel,
                outputFolder,
                true,
                "Validation passed (1 stage).",
                null,
                null,
                System.DateTimeOffset.UtcNow.AddSeconds(-1),
                System.DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult(_stageId, _stageLabel, "passed", "Build succeeded.", logPath, 0, 10)
                },
                "passed",
                "Passed cleanly",
                null,
                null,
                Path.Combine(outputFolder, "validation_stability.json"),
                action == ValidationAction.RunFullValidationLoop ? "sequential_standard_mode" : "single_stage_manual_mode");
            progress?.Invoke(new ValidationProgressEvent("stage_completed", _stageId, _stageLabel, "completed", "Build succeeded.", null, logPath, outputFolder, System.DateTimeOffset.UtcNow));
            progress?.Invoke(new ValidationProgressEvent("run_completed", string.Empty, _actionLabel, "completed", result.Summary, null, null, outputFolder, System.DateTimeOffset.UtcNow));
            return result;
        }

        public Task WaitForStartAsync() => _started.Task;

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class SequencedValidationRunnerService : IValidationRunnerService
    {
        private readonly Queue<ValidationRunResult> _results;

        public SequencedValidationRunnerService(string repoRoot, IEnumerable<ValidationRunResult> results)
        {
            RepoRoot = repoRoot;
            ValidationRunsRoot = Path.Combine(repoRoot, ".codex", "validation-ui", "runs");
            _results = new Queue<ValidationRunResult>(results);
        }

        public string RepoRoot { get; }
        public string ValidationRunsRoot { get; }

        public IReadOnlyList<string> GetStageLabels(ValidationAction action, bool includeValidateBuild)
            => new[] { "Running UI tests" };

        public IReadOnlyList<ValidationRunSummary> LoadRecentRuns(int maxCount)
            => System.Array.Empty<ValidationRunSummary>();

        public Task<ValidationRunResult> RunAsync(ValidationAction action, ValidationSettings settings, Action<ValidationProgressEvent>? progress = null, CancellationToken ct = default)
        {
            var result = _results.Dequeue();
            progress?.Invoke(new ValidationProgressEvent("run_started", string.Empty, result.ActionLabel, "active", result.Summary, null, null, result.OutputFolder, System.DateTimeOffset.UtcNow));
            foreach (var stage in result.Stages)
            {
                progress?.Invoke(new ValidationProgressEvent("stage_started", stage.StageId, stage.StageLabel, "active", $"{stage.StageLabel} started.", null, stage.LogPath, result.OutputFolder, System.DateTimeOffset.UtcNow));
                progress?.Invoke(new ValidationProgressEvent("stage_completed", stage.StageId, stage.StageLabel, stage.Status == "failed" ? "failed" : "completed", stage.Summary, null, stage.LogPath, result.OutputFolder, System.DateTimeOffset.UtcNow));
            }

            progress?.Invoke(new ValidationProgressEvent("run_completed", string.Empty, result.ActionLabel, result.Success ? "completed" : "failed", result.Summary, null, result.FirstFailureLogPath, result.OutputFolder, System.DateTimeOffset.UtcNow));
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRepairAttemptService : IRepairAttemptService
    {
        private readonly IReadOnlyList<string> _changedFiles;

        public RecordingRepairAttemptService(string repoRoot, IReadOnlyList<string> changedFiles)
        {
            RepairsRoot = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs");
            _changedFiles = changedFiles;
        }

        public string RepairsRoot { get; }
        public string? BundlePath { get; private set; }

        public Task<RepairAttemptResult> AttemptRepairAsync(RepairBundle bundle, CancellationToken ct = default)
        {
            var repairFolder = Path.Combine(RepairsRoot, bundle.RepairId);
            Directory.CreateDirectory(repairFolder);
            BundlePath = Path.Combine(repairFolder, "repair_bundle.json");
            File.WriteAllText(BundlePath, System.Text.Json.JsonSerializer.Serialize(bundle));

            return Task.FromResult(new RepairAttemptResult(
                bundle.RepairId,
                repairFolder,
                "Repair applied deterministic changes.",
                _changedFiles,
                "changed",
                System.DateTimeOffset.UtcNow));
        }
    }

    private sealed class DeterministicEnvironmentProfile : IEnvironmentProfile
    {
        public string Name => "deterministic";
        public string Description => "Deterministic test profile";
        public EnvironmentCapability DeclaredCapabilities => EnvironmentCapability.None;
        public IReadOnlyList<SandboxPreparationStep> SandboxPreparationSteps => System.Array.Empty<SandboxPreparationStep>();
    }

    private sealed class DeterministicWorkspaceProvider : IProjectWorkspaceProvider
    {
        private readonly List<ProjectWorkspace> _workspaces;
        private ProjectWorkspace? _active;

        public DeterministicWorkspaceProvider()
        {
            _workspaces = new List<ProjectWorkspace>();

            _active = new ProjectWorkspace(
                Name: "deterministic-workspace",
                RootPath: "deterministic-workspace",
                LastOpenedUtc: System.DateTimeOffset.UtcNow,
                ProjectId: "deterministic-project");
            _workspaces.Add(_active);
        }

        public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces() => _workspaces;

        public ProjectWorkspace? GetActiveWorkspace() => _active;

        public void SetActiveWorkspace(ProjectWorkspace workspace)
        {
            _active = workspace;
            if (!_workspaces.Contains(workspace))
            {
                _workspaces.Add(workspace);
            }
        }

        public void RemoveWorkspace(ProjectWorkspace workspace)
        {
            _workspaces.Remove(workspace);
            if (ReferenceEquals(_active, workspace))
            {
                _active = null;
            }
        }

        public void UpdateWorkspace(ProjectWorkspace workspace)
        {
            var index = _workspaces.FindIndex(existing =>
                string.Equals(existing.RootPath, workspace.RootPath, System.StringComparison.Ordinal));
            if (index >= 0)
            {
                _workspaces[index] = workspace;
            }

            if (_active is not null && string.Equals(_active.RootPath, workspace.RootPath, System.StringComparison.Ordinal))
            {
                _active = workspace;
            }
        }
    }

    private sealed class NullWorkspaceShellService : IWorkspaceShellService
    {
        public bool OpenFolder(string path) => true;

        public Task OpenFolderAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task CopyTextAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    }


    private sealed class RecordingWorkspaceShellService : IWorkspaceShellService
    {
        public List<string> OpenedPaths { get; } = new();
        public List<string> CopiedTexts { get; } = new();

        public bool OpenFolder(string path)
        {
            OpenedPaths.Add(path);
            return true;
        }

        public Task OpenFolderAsync(string path, CancellationToken ct = default)
        {
            OpenedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task CopyTextAsync(string text, CancellationToken ct = default)
        {
            CopiedTexts.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedValidationCommandExecutor : IValidationCommandExecutor
    {
        private readonly IReadOnlyDictionary<string, ValidationCommandExecutionResult> _results;

        public ScriptedValidationCommandExecutor(IReadOnlyDictionary<string, ValidationCommandExecutionResult> results)
        {
            _results = results;
        }

        public Task<ValidationCommandExecutionResult> ExecuteAsync(
            ValidationCommandSpec command,
            string workingDirectory,
            string logPath,
            Action<string> onOutput,
            CancellationToken ct)
        {
            var result = _results.TryGetValue(command.StageId, out var mapped)
                ? mapped
                : new ValidationCommandExecutionResult(0, System.Array.Empty<string>());

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, result.OutputLines);
            foreach (var line in result.OutputLines)
            {
                onOutput(line);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class SuccessfulBuilderProofCommandRunner : IBuilderProofCommandRunner
    {
        public Task<BuilderProofCommandExecutionResult> ExecuteAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            CancellationToken ct)
        {
            var isProofCalc = arguments.Any(argument => argument.Contains("ProofCalc.Tests.csproj", StringComparison.OrdinalIgnoreCase));
            var isTestExtension = arguments.Any(argument => argument.Contains("ExtensionCalc.Tests.csproj", StringComparison.OrdinalIgnoreCase));
            var isSplitRefactorProbe = workingDirectory.Contains($"comparative-proof{Path.DirectorySeparatorChar}split-floor", StringComparison.OrdinalIgnoreCase) ||
                                       workingDirectory.Contains("bounded-refactor-split", StringComparison.OrdinalIgnoreCase);
            var isComparativeRefactorProbe = workingDirectory.Contains($"comparative-proof{Path.DirectorySeparatorChar}stronger-tier", StringComparison.OrdinalIgnoreCase);
            var isRefactorProbe = arguments.Any(argument => argument.Contains("RefactorProof.csproj", StringComparison.OrdinalIgnoreCase)) &&
                                  !isSplitRefactorProbe &&
                                  !isComparativeRefactorProbe;
            var isRecovery = logPath.Contains($"{Path.DirectorySeparatorChar}recovery{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            var isTestCommand = arguments.Count > 0 && string.Equals(arguments[0], "test", StringComparison.Ordinal);

            var lines = isRefactorProbe
                ? new[]
                {
                    "ProfileSummary.cs(7,20): error CS0103: The name 'NameFormatter' does not exist in the current context",
                    "Build FAILED."
                }
                : isTestExtension && !isRecovery
                    ? new[]
                    {
                        "CalculatorExtensionTests.cs(10,35): error CS0103: The name 'Calculator' does not exist in the current context",
                        "Build FAILED."
                    }
                : isProofCalc && !isRecovery
                ? new[]
                {
                    "ProofCalc/Calculator.cs(7,36): error CS1002: ; expected",
                    "Build FAILED."
                }
                : isTestCommand
                    ? new[]
                    {
                        "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
                    }
                    : new[]
                    {
                        "Build succeeded."
                    };

            var exitCode = (isProofCalc && !isRecovery) || (isTestExtension && !isRecovery) || isRefactorProbe ? 1 : 0;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Join(System.Environment.NewLine, lines));
            return Task.FromResult(new BuilderProofCommandExecutionResult(exitCode, lines));
        }
    }

    private sealed class ScriptedBuilderToolchainCapabilityScanner : IBuilderToolchainCapabilityScanner
    {
        private readonly BuilderToolchainCapabilityObservation[] _observations;

        public ScriptedBuilderToolchainCapabilityScanner(params BuilderToolchainCapabilityObservation[] observations)
        {
            _observations = observations;
        }

        public IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot) => _observations;
    }

    private sealed class ScriptedBuilderGitReadinessProbe : IBuilderGitReadinessProbe
    {
        private readonly BuilderGitReadinessObservation _observation;

        public ScriptedBuilderGitReadinessProbe(BuilderGitReadinessObservation observation)
        {
            _observation = observation;
        }

        public BuilderGitReadinessObservation Probe(string repoRoot) => _observation;
    }

    private sealed class AvailableBuilderStrongerTierResolver : IBuilderStrongerTierResolver
    {
        public Task<BuilderStrongerTierAvailability> ResolveAsync(
            string currentModelId,
            string recommendedModelClass,
            string? preferredStrongerModelId,
            string provider,
            CancellationToken ct)
            => Task.FromResult(new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                "qwen2.5:7b-instruct",
                "available",
                "qwen2.5:7b-instruct is available for bounded comparative proof.",
                provider,
                "http://localhost:11434",
                string.Empty,
                new[] { BuilderExecutionService.BuilderProofFloorModelId, "qwen2.5:7b-instruct" },
                "Resolved qwen2.5:7b-instruct from the bounded stronger-tier candidate set.",
                Array.Empty<string>(),
                "qwen2.5:7b-instruct is available for bounded comparative proof.",
                string.Empty,
                System.DateTimeOffset.UtcNow));
    }

    private sealed class InMemoryAiPolicyStore : IAiPolicyStore
    {
        private AiPolicySettings _settings = new(
            AiAccessRole.Developer,
            new AiPresentationPolicy(
                AiVisibilityMode.Visible,
                AllowAiPanelToggle: true,
                AllowCopyExport: true,
                EnterpriseMode: false));

        public AiPolicySettings Load(string? workspaceRoot) => _settings;

        public void Save(string? workspaceRoot, AiPolicySettings settings)
        {
            _settings = settings;
        }
    }

    private sealed class FixedBackendProbeService : IBackendProbeService
    {
        private readonly BackendStatus _ollama;
        private readonly BackendStatus _qdrant;

        public FixedBackendProbeService(BackendStatus ollama, BackendStatus qdrant)
        {
            _ollama = ollama;
            _qdrant = qdrant;
        }

        public Task<BackendStatus> ProbeOllamaAsync(CancellationToken cancellationToken) => Task.FromResult(_ollama);

        public Task<BackendStatus> ProbeQdrantAsync(CancellationToken cancellationToken) => Task.FromResult(_qdrant);
    }

    private sealed class FixedOllamaClient : IOllamaClient
    {
        private readonly OllamaTagsResult _result;

        public FixedOllamaClient(OllamaTagsResult result)
        {
            _result = result;
        }

        public Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken) => Task.FromResult(_result);
    }

    private sealed class BlockingBackendProbeService : IBackendProbeService
    {
        private readonly TaskCompletionSource<bool> _probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BackendStatus> ProbeOllamaAsync(CancellationToken cancellationToken)
        {
            _probeStarted.TrySetResult(true);
            await _release.Task;
            return new BackendStatus(BackendKind.Ollama, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:11434", null);
        }

        public Task<BackendStatus> ProbeQdrantAsync(CancellationToken cancellationToken)
            => Task.FromResult(new BackendStatus(BackendKind.Qdrant, true, null, "ok", System.DateTimeOffset.UtcNow, "http://localhost:6333", null));

        public Task WaitForProbeStartAsync() => _probeStarted.Task;

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class SequenceOllamaClient : IOllamaClient
    {
        private readonly Queue<OllamaTagsResult> _results;

        public SequenceOllamaClient(params OllamaTagsResult[] results)
        {
            _results = new Queue<OllamaTagsResult>(results);
        }

        public Task<OllamaTagsResult> GetTagsAsync(CancellationToken cancellationToken)
        {
            if (_results.Count == 0)
            {
                return Task.FromResult(new OllamaTagsResult(true, System.Array.Empty<string>(), null, "ok"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FailingPlanner : Shoots.UI.Builder.IPlanner
    {
        public bool TryBuildPlan(ProjectModel project, out Shoots.UI.Builder.PlanModel plan)
        {
            plan = new Shoots.UI.Builder.PlanModel("none", Shoots.UI.Builder.PlanSourceType.Demo, new List<Shoots.UI.Builder.PlanStep>());
            return false;
        }
    }
}
