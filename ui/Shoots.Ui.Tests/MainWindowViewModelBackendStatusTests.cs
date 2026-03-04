using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
            new FixedOllamaClient(new OllamaTagsResult(false, new string[0], "ui.ollama.unreachable", "Ollama unavailable.")));

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
            new FixedOllamaClient(new OllamaTagsResult(false, new string[0], "ui.ollama.unreachable", "Ollama unavailable.")));

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
            new FixedOllamaClient(new OllamaTagsResult(true, new[] { "llama3" }, null, "ok")));

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
    private static MainWindowViewModel BuildViewModel(IBackendProbeService probeService, IOllamaClient ollamaClient)
    {
        var workspaceStore = new ProjectWorkspaceStore();
        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new EnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            new ProjectWorkspaceProvider(workspaceStore),
            new WorkspaceShellService(),
            new DatabaseIntentStore(),
            new ToolTierPrompt(),
            new SystemBlueprintStore(),
            new ExecutionEnvironmentSettingsStore(),
            new AiPolicyStore(),
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            probeService,
            ollamaClient);
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
                return Task.FromResult(new OllamaTagsResult(true, new string[0], null, "ok"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
