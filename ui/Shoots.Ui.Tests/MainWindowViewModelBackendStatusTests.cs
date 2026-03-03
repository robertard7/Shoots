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
}
