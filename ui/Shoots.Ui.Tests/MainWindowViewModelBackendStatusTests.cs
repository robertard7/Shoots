using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core.AI;
using Shoots.UI.Blueprints;
using Shoots.UI.Environment;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
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

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
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

    private static MainWindowViewModel BuildViewModel(
        IBackendProbeService probeService,
        IOllamaClient ollamaClient,
        bool includeProfile = true,
        IWorkspaceShellService? workspaceShell = null,
        Shoots.UI.Builder.IPlanner? planner = null)
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
            planner: planner,
            autoRefreshBackends: false);
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
