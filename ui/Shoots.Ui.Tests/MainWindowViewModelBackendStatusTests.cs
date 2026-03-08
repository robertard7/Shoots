using System.Collections.Generic;
using System.IO;
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

        probeService.Release();
        await refreshTask;
    }



    [Fact]
    public async Task Copy_artifact_path_commands_route_through_workspace_shell_service()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shoots-copy-test-{System.Guid.NewGuid():N}");
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
                new OllamaTagsResult(true, new[] { "model-c", "model-d" }, null, "ok")));

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
    private static MainWindowViewModel BuildViewModel(
        IBackendProbeService probeService,
        IOllamaClient ollamaClient,
        bool includeProfile = true,
        IWorkspaceShellService? workspaceShell = null)
    {
        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new DeterministicEnvironmentProfileService(includeProfile),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            new DeterministicWorkspaceProvider(),
            workspaceShell ?? new NullWorkspaceShellService(),
            new DatabaseIntentStore(),
            new ToolTierPrompt(),
            new SystemBlueprintStore(),
            new ExecutionEnvironmentSettingsStore(),
            new InMemoryAiPolicyStore(),
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            probeService,
            ollamaClient);
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
}
