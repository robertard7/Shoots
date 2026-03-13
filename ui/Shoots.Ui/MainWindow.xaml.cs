using System.Windows;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Blueprints;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Shoots.UI.Builder;
using Shoots.UI.Services.Backends;
using Shoots.UI.ViewModels;
using System;
using System.Net.Http;

namespace Shoots.UI;

// DO NOT ADD LOGIC HERE.
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var workspaceStore = new ProjectWorkspaceStore();
        var workspaceProvider = new ProjectWorkspaceProvider(workspaceStore);
        var workspaceShell = new WorkspaceShellService();
        var databaseIntentStore = new DatabaseIntentStore();
        var blueprintStore = new SystemBlueprintStore();
        var executionEnvironmentStore = new ExecutionEnvironmentSettingsStore();
        var aiPolicyStore = new AiPolicyStore();
        var ollamaHttpClient = new HttpClient
        {
            BaseAddress = new Uri(EndpointResolver.ResolveOllamaEndpoint(), UriKind.Absolute)
        };
        var qdrantHttpClient = new HttpClient
        {
            BaseAddress = new Uri(EndpointResolver.ResolveQdrantEndpoint(), UriKind.Absolute)
        };

        var ollamaClient = new OllamaClient(ollamaHttpClient);
        var qdrantClient = new QdrantClient(qdrantHttpClient);
        var semanticReuseService = new SemanticReuseService(vectorStore: new QdrantSemanticReuseStore(qdrantHttpClient));
        var backendProbeService = new BackendProbeService(ollamaClient, qdrantClient);
        var toolRegistry = new ToolRegistry();
        var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(toolRegistry));
        var builderExecutionService = new BuilderExecutionService(
            runtimeBridge,
            new ArtifactManager(),
            toolRegistry,
            builderStrongerTierResolver: new OllamaBuilderStrongerTierResolver(ollamaClient, EndpointResolver.ResolveOllamaEndpoint()));

        DataContext = new MainWindowViewModel(
            new NullExecutionCommandService(),
            new EnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            workspaceProvider,
            workspaceShell,
            databaseIntentStore,
            new ToolTierPrompt(),
            blueprintStore,
            executionEnvironmentStore,
            aiPolicyStore,
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            backendProbeService,
            ollamaClient,
            builderExecutionService: builderExecutionService,
            semanticReuseService: semanticReuseService);
    }
}
