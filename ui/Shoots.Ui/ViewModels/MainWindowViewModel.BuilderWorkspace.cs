using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderWorkspaceOptionRow> _builderWorkspaceOptions = new();
    private BuilderWorkspaceSurfaceContext? _builderWorkspaceSurfaceContext;
    private string _selectedBuilderWorkspaceId = string.Empty;
    private string _builderWorkspaceBadge = "No builder workspace";
    private string _builderWorkspaceRepoRoot = string.Empty;
    private string _builderWorkspaceRegistrySummary = "No builder workspace registry recorded.";
    private string _builderWorkspaceCapabilitySummary = "No builder workspace capabilities recorded.";
    private string _builderWorkspaceContextSummary = "No builder workspace context resolved.";
    private string _builderWorkspaceRouteResolutionSummary = "No builder workspace route resolution recorded.";
    private string _builderWorkspaceRegistryArtifactPath = string.Empty;
    private string _builderWorkspaceContextArtifactPath = string.Empty;
    private string _builderWorkspaceCapabilitiesArtifactPath = string.Empty;
    private string _builderWorkspaceRouteResolutionArtifactPath = string.Empty;
    private bool _isRefreshingBuilderWorkspace;

    public ReadOnlyObservableCollection<BuilderWorkspaceOptionRow> BuilderWorkspaceOptions { get; private set; } = null!;
    public bool HasBuilderWorkspaceOptions => _builderWorkspaceOptions.Count > 0;

    public string SelectedBuilderWorkspaceId
    {
        get => _selectedBuilderWorkspaceId;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(_selectedBuilderWorkspaceId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderWorkspaceId = normalized;
            OnPropertyChanged(nameof(SelectedBuilderWorkspaceId));
            LoadBuilderWorkspaceArtifacts();
            LoadBuilderProofArtifacts();
            LoadBuilderExternalReconArtifacts();
            LoadBuilderPatternLibraryArtifacts();
            LoadBuilderPatternPatchArtifacts();
        }
    }

    public string BuilderWorkspaceBadge => _builderWorkspaceBadge;
    public string BuilderWorkspaceRepoRoot => _builderWorkspaceRepoRoot;
    public string BuilderWorkspaceRegistrySummary => _builderWorkspaceRegistrySummary;
    public bool HasBuilderWorkspaceRegistrySummary => !string.IsNullOrWhiteSpace(_builderWorkspaceRegistrySummary) &&
                                                      !string.Equals(_builderWorkspaceRegistrySummary, "No builder workspace registry recorded.", StringComparison.Ordinal);
    public string BuilderWorkspaceCapabilitySummary => _builderWorkspaceCapabilitySummary;
    public bool HasBuilderWorkspaceCapabilitySummary => !string.IsNullOrWhiteSpace(_builderWorkspaceCapabilitySummary) &&
                                                        !string.Equals(_builderWorkspaceCapabilitySummary, "No builder workspace capabilities recorded.", StringComparison.Ordinal);
    public string BuilderWorkspaceContextSummary => _builderWorkspaceContextSummary;
    public bool HasBuilderWorkspaceContextSummary => !string.IsNullOrWhiteSpace(_builderWorkspaceContextSummary) &&
                                                     !string.Equals(_builderWorkspaceContextSummary, "No builder workspace context resolved.", StringComparison.Ordinal);
    public string BuilderWorkspaceRouteResolutionSummary => _builderWorkspaceRouteResolutionSummary;
    public bool HasBuilderWorkspaceRouteResolutionSummary => !string.IsNullOrWhiteSpace(_builderWorkspaceRouteResolutionSummary) &&
                                                             !string.Equals(_builderWorkspaceRouteResolutionSummary, "No builder workspace route resolution recorded.", StringComparison.Ordinal);
    public string BuilderWorkspaceRegistryArtifactPath => _builderWorkspaceRegistryArtifactPath;
    public bool HasBuilderWorkspaceRegistryArtifactPath => !string.IsNullOrWhiteSpace(_builderWorkspaceRegistryArtifactPath) && File.Exists(_builderWorkspaceRegistryArtifactPath);
    public string BuilderWorkspaceContextArtifactPath => _builderWorkspaceContextArtifactPath;
    public bool HasBuilderWorkspaceContextArtifactPath => !string.IsNullOrWhiteSpace(_builderWorkspaceContextArtifactPath) && File.Exists(_builderWorkspaceContextArtifactPath);
    public string BuilderWorkspaceCapabilitiesArtifactPath => _builderWorkspaceCapabilitiesArtifactPath;
    public bool HasBuilderWorkspaceCapabilitiesArtifactPath => !string.IsNullOrWhiteSpace(_builderWorkspaceCapabilitiesArtifactPath) && File.Exists(_builderWorkspaceCapabilitiesArtifactPath);
    public string BuilderWorkspaceRouteResolutionArtifactPath => _builderWorkspaceRouteResolutionArtifactPath;
    public bool HasBuilderWorkspaceRouteResolutionArtifactPath => !string.IsNullOrWhiteSpace(_builderWorkspaceRouteResolutionArtifactPath) && File.Exists(_builderWorkspaceRouteResolutionArtifactPath);

    private void InitializeBuilderWorkspaceSurface()
    {
        BuilderWorkspaceOptions = new ReadOnlyObservableCollection<BuilderWorkspaceOptionRow>(_builderWorkspaceOptions);
        RefreshBuilderWorkspaceOptions();
        ResetBuilderWorkspaceState();
    }

    private void RefreshBuilderWorkspaceOptions()
    {
        var rows = new List<BuilderWorkspaceOptionRow>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validationRepoRoot = NormalizeExistingBuilderWorkspaceRoot(_validationRunnerService.RepoRoot);

        void AddWorkspace(string? repoRoot, string? displayName, bool allowFallback)
        {
            var normalizedRoot = NormalizeExistingBuilderWorkspaceRoot(repoRoot);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !seenRoots.Add(normalizedRoot))
            {
                return;
            }

            if (!allowFallback && !BuilderWorkspaceService.IsWorkspaceRootEligible(normalizedRoot))
            {
                return;
            }

            var descriptor = BuilderWorkspaceService.CreateDescriptor(normalizedRoot, displayName);
            rows.Add(new BuilderWorkspaceOptionRow(
                descriptor.WorkspaceId,
                string.IsNullOrWhiteSpace(displayName) ? descriptor.RepoName : displayName.Trim(),
                descriptor.RepoName,
                descriptor.RepoRootPath));
        }

        AddWorkspace(validationRepoRoot, Path.GetFileName(validationRepoRoot), allowFallback: true);
        AddWorkspace(ActiveWorkspace?.RootPath, ActiveWorkspace?.Name, allowFallback: false);
        foreach (var workspace in RecentWorkspaces)
        {
            AddWorkspace(workspace.RootPath, workspace.Name, allowFallback: false);
        }

        var orderedRows = rows
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RepoRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _builderWorkspaceOptions.Clear();
        foreach (var row in orderedRows)
        {
            _builderWorkspaceOptions.Add(row);
        }

        var preferredWorkspaceId = ResolvePreferredBuilderWorkspaceId(validationRepoRoot);
        if (!string.Equals(_selectedBuilderWorkspaceId, preferredWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBuilderWorkspaceId = preferredWorkspaceId;
            OnPropertyChanged(nameof(SelectedBuilderWorkspaceId));
        }

        OnPropertyChanged(nameof(HasBuilderWorkspaceOptions));
    }

    private void LoadBuilderWorkspaceArtifacts()
    {
        if (_isRefreshingBuilderWorkspace)
        {
            return;
        }

        RefreshBuilderWorkspaceOptions();
        if (_builderWorkspaceOptions.Count == 0)
        {
            ResetBuilderWorkspaceState();
            ResetBuilderExternalReconState();
            ResetBuilderPatternPatchState();
            return;
        }

        _isRefreshingBuilderWorkspace = true;
        try
        {
            var descriptors = _builderWorkspaceOptions
                .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
                .ToArray();
            var selectedRoot = ResolveSelectedBuilderWorkspaceRepoRoot();
            var context = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                descriptors,
                new BuilderWorkspaceResolutionRequest(
                    ExplicitWorkspaceId: _selectedBuilderWorkspaceId,
                    ExplicitRepoRoot: selectedRoot ?? string.Empty,
                    CurrentWorkingDirectory: GetCurrentWorkingDirectorySafe()),
                _builderToolchainCapabilityScanner);
            if (context is null)
            {
                ResetBuilderWorkspaceState();
                return;
            }

            ApplyBuilderWorkspaceContext(context);
        }
        finally
        {
            _isRefreshingBuilderWorkspace = false;
        }

        NotifyBuilderWorkspaceStateChanged();
    }

    private void ApplyBuilderWorkspaceContext(BuilderWorkspaceSurfaceContext context)
    {
        _builderWorkspaceSurfaceContext = context;
        if (!string.Equals(_selectedBuilderWorkspaceId, context.Context.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBuilderWorkspaceId = context.Context.ActiveWorkspaceId;
            OnPropertyChanged(nameof(SelectedBuilderWorkspaceId));
        }

        _builderWorkspaceBadge = $"{context.Context.RepoName} ({context.Context.ActiveWorkspaceId})";
        _builderWorkspaceRepoRoot = context.Context.RepoRoot;
        _builderWorkspaceRegistrySummary = context.Registry.Summary;
        _builderWorkspaceCapabilitySummary = context.Capabilities.Summary;
        _builderWorkspaceContextSummary = context.Context.Summary;
        _builderWorkspaceRouteResolutionSummary = context.RouteResolution?.Summary ?? "No builder workspace route resolution recorded.";
        _builderWorkspaceRegistryArtifactPath = context.Registry.ArtifactPath;
        _builderWorkspaceContextArtifactPath = context.Context.ArtifactPath;
        _builderWorkspaceCapabilitiesArtifactPath = context.Capabilities.ArtifactPath;
        _builderWorkspaceRouteResolutionArtifactPath = context.RouteResolution?.ArtifactPath ?? BuilderWorkspaceService.RouteResolutionPathForRepo(context.Context.RepoRoot);
    }

    private void ResetBuilderWorkspaceState()
    {
        _builderWorkspaceSurfaceContext = null;
        _builderWorkspaceBadge = "No builder workspace";
        _builderWorkspaceRepoRoot = string.Empty;
        _builderWorkspaceRegistrySummary = "No builder workspace registry recorded.";
        _builderWorkspaceCapabilitySummary = "No builder workspace capabilities recorded.";
        _builderWorkspaceContextSummary = "No builder workspace context resolved.";
        _builderWorkspaceRouteResolutionSummary = "No builder workspace route resolution recorded.";
        _builderWorkspaceRegistryArtifactPath = string.Empty;
        _builderWorkspaceContextArtifactPath = string.Empty;
        _builderWorkspaceCapabilitiesArtifactPath = string.Empty;
        _builderWorkspaceRouteResolutionArtifactPath = string.Empty;
        ResetBuilderExternalReconState();
        ResetBuilderPatternPatchState();
        NotifyBuilderWorkspaceStateChanged();
    }

    private void NotifyBuilderWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(BuilderWorkspaceOptions));
        OnPropertyChanged(nameof(HasBuilderWorkspaceOptions));
        OnPropertyChanged(nameof(SelectedBuilderWorkspaceId));
        OnPropertyChanged(nameof(BuilderWorkspaceBadge));
        OnPropertyChanged(nameof(BuilderWorkspaceRepoRoot));
        OnPropertyChanged(nameof(BuilderWorkspaceRegistrySummary));
        OnPropertyChanged(nameof(HasBuilderWorkspaceRegistrySummary));
        OnPropertyChanged(nameof(BuilderWorkspaceCapabilitySummary));
        OnPropertyChanged(nameof(HasBuilderWorkspaceCapabilitySummary));
        OnPropertyChanged(nameof(BuilderWorkspaceContextSummary));
        OnPropertyChanged(nameof(HasBuilderWorkspaceContextSummary));
        OnPropertyChanged(nameof(BuilderWorkspaceRouteResolutionSummary));
        OnPropertyChanged(nameof(HasBuilderWorkspaceRouteResolutionSummary));
        OnPropertyChanged(nameof(BuilderWorkspaceRegistryArtifactPath));
        OnPropertyChanged(nameof(HasBuilderWorkspaceRegistryArtifactPath));
        OnPropertyChanged(nameof(BuilderWorkspaceContextArtifactPath));
        OnPropertyChanged(nameof(HasBuilderWorkspaceContextArtifactPath));
        OnPropertyChanged(nameof(BuilderWorkspaceCapabilitiesArtifactPath));
        OnPropertyChanged(nameof(HasBuilderWorkspaceCapabilitiesArtifactPath));
        OnPropertyChanged(nameof(BuilderWorkspaceRouteResolutionArtifactPath));
        OnPropertyChanged(nameof(HasBuilderWorkspaceRouteResolutionArtifactPath));
        OnPropertyChanged(nameof(BuilderProofDisabledReason));
    }

    private string GetBuilderWorkspaceRepoRoot()
        => _builderWorkspaceSurfaceContext?.Context.RepoRoot
           ?? ResolveSelectedBuilderWorkspaceRepoRoot()
           ?? _validationRunnerService.RepoRoot;

    private void RecordBuilderWorkspaceRouteResolution(string requestId, string routeDecision)
    {
        if (_builderWorkspaceSurfaceContext?.Context is null)
        {
            LoadBuilderWorkspaceArtifacts();
        }

        if (_builderWorkspaceSurfaceContext?.Context is null)
        {
            return;
        }

        var routeResolution = BuilderWorkspaceService.RecordRouteResolution(
            _builderWorkspaceSurfaceContext.Context,
            requestId,
            routeDecision);
        _builderWorkspaceSurfaceContext = _builderWorkspaceSurfaceContext with
        {
            RouteResolution = routeResolution
        };
        _builderWorkspaceRouteResolutionSummary = routeResolution.Summary;
        _builderWorkspaceRouteResolutionArtifactPath = routeResolution.ArtifactPath;
        NotifyBuilderWorkspaceStateChanged();
    }

    private string ResolvePreferredBuilderWorkspaceId(string validationRepoRoot)
    {
        if (_builderWorkspaceOptions.Any(option => string.Equals(option.WorkspaceId, _selectedBuilderWorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            return _selectedBuilderWorkspaceId;
        }

        var activeRoot = NormalizeExistingBuilderWorkspaceRoot(ActiveWorkspace?.RootPath);
        if (!string.IsNullOrWhiteSpace(activeRoot))
        {
            var activeMatch = _builderWorkspaceOptions.FirstOrDefault(option =>
                string.Equals(option.RepoRoot, activeRoot, StringComparison.OrdinalIgnoreCase));
            if (activeMatch is not null)
            {
                return activeMatch.WorkspaceId;
            }
        }

        if (!string.IsNullOrWhiteSpace(validationRepoRoot))
        {
            var validationMatch = _builderWorkspaceOptions.FirstOrDefault(option =>
                string.Equals(option.RepoRoot, validationRepoRoot, StringComparison.OrdinalIgnoreCase));
            if (validationMatch is not null)
            {
                return validationMatch.WorkspaceId;
            }
        }

        return _builderWorkspaceOptions.FirstOrDefault()?.WorkspaceId ?? string.Empty;
    }

    private string? ResolveSelectedBuilderWorkspaceRepoRoot()
        => _builderWorkspaceOptions
            .FirstOrDefault(option => string.Equals(option.WorkspaceId, _selectedBuilderWorkspaceId, StringComparison.OrdinalIgnoreCase))
            ?.RepoRoot;

    private static string GetCurrentWorkingDirectorySafe()
    {
        try
        {
            return System.Environment.CurrentDirectory;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeExistingBuilderWorkspaceRoot(string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return string.Empty;
        }

        try
        {
            var normalized = Path.GetFullPath(repoRoot);
            return Directory.Exists(normalized) ? normalized : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed record BuilderWorkspaceOptionRow(
    string WorkspaceId,
    string Label,
    string RepoName,
    string RepoRoot)
{
    public string Summary => $"{Label} - {RepoRoot}";
}
