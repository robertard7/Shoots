using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderPatternPatchCandidateRow> _builderPatternPatchCandidates = new();
    private readonly ObservableCollection<string> _builderPatternPatchSelectedTransformationSteps = new();
    private readonly ObservableCollection<string> _builderPatternPatchSelectedSourceElements = new();
    private readonly ObservableCollection<string> _builderPatternPatchSelectedTargetElements = new();
    private readonly ObservableCollection<string> _builderPatternPatchSelectedMappingRules = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderPatternPatchSelectedArtifactLinks = new();
    private BuilderPatternPatchCandidateRow[] _builderPatternPatchAllCandidates = Array.Empty<BuilderPatternPatchCandidateRow>();
    private BuilderPatternPatchCandidateRow? _selectedBuilderPatternPatchCandidate;
    private string _builderPatternPatchSummary = "No synthesized pattern patch candidates recorded.";
    private string _builderPatternPatchArtifactPath = string.Empty;
    private string _builderPatternPatchExplanationArtifactPath = string.Empty;
    private string _builderPatternPatchProvenanceArtifactPath = string.Empty;
    private string _builderPatternPatchMatchArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderPatternPatchCandidateRow> BuilderPatternPatchCandidates { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternPatchSelectedTransformationSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternPatchSelectedSourceElements { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternPatchSelectedTargetElements { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternPatchSelectedMappingRules { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderPatternPatchSelectedArtifactLinks { get; private set; } = null!;

    public bool HasBuilderPatternPatchCandidates => _builderPatternPatchCandidates.Count > 0;
    public string BuilderPatternPatchSummary => _builderPatternPatchSummary;
    public string BuilderPatternPatchArtifactPath => _builderPatternPatchArtifactPath;
    public string BuilderPatternPatchExplanationArtifactPath => _builderPatternPatchExplanationArtifactPath;
    public string BuilderPatternPatchProvenanceArtifactPath => _builderPatternPatchProvenanceArtifactPath;
    public string BuilderPatternPatchMatchArtifactPath => _builderPatternPatchMatchArtifactPath;
    public bool HasBuilderPatternPatchArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternPatchArtifactPath) && File.Exists(_builderPatternPatchArtifactPath);
    public bool HasBuilderPatternPatchExplanationArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternPatchExplanationArtifactPath) && File.Exists(_builderPatternPatchExplanationArtifactPath);
    public bool HasBuilderPatternPatchProvenanceArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternPatchProvenanceArtifactPath) && File.Exists(_builderPatternPatchProvenanceArtifactPath);
    public bool HasBuilderPatternPatchMatchArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternPatchMatchArtifactPath) && File.Exists(_builderPatternPatchMatchArtifactPath);
    public bool HasSelectedBuilderPatternPatchCandidate => _selectedBuilderPatternPatchCandidate is not null;
    public string BuilderPatternPatchSelectedTitle => _selectedBuilderPatternPatchCandidate?.Header ?? "No synthesized patch candidate selected.";
    public string BuilderPatternPatchSelectedSummary => _selectedBuilderPatternPatchCandidate?.Summary ?? "No synthesized patch candidate selected.";
    public string BuilderPatternPatchSelectedTargetSummary => _selectedBuilderPatternPatchCandidate?.TargetSummary ?? string.Empty;
    public string BuilderPatternPatchSelectedEligibilitySummary => _selectedBuilderPatternPatchCandidate?.EligibilitySummary ?? string.Empty;
    public string BuilderPatternPatchSelectedRiskSummary => _selectedBuilderPatternPatchCandidate?.RiskSummary ?? string.Empty;
    public string BuilderPatternPatchSelectedLicenseSummary => _selectedBuilderPatternPatchCandidate?.LicenseSummary ?? string.Empty;
    public string BuilderPatternPatchSelectedProvenanceSummary => _selectedBuilderPatternPatchCandidate?.ProvenanceSummary ?? string.Empty;
    public string BuilderPatternPatchSelectedDiffText => _selectedBuilderPatternPatchCandidate?.DiffPreview ?? "No diff preview recorded.";
    public bool HasBuilderPatternPatchSelectedTransformationSteps => _builderPatternPatchSelectedTransformationSteps.Count > 0;
    public bool HasBuilderPatternPatchSelectedSourceElements => _builderPatternPatchSelectedSourceElements.Count > 0;
    public bool HasBuilderPatternPatchSelectedTargetElements => _builderPatternPatchSelectedTargetElements.Count > 0;
    public bool HasBuilderPatternPatchSelectedMappingRules => _builderPatternPatchSelectedMappingRules.Count > 0;
    public bool HasBuilderPatternPatchSelectedArtifactLinks => _builderPatternPatchSelectedArtifactLinks.Count > 0;
    public bool CanGenerateBuilderPatternPatches => HasBuilderPatternLibraryEntries;
    public bool CanStageBuilderPatternPatchCandidate => _selectedBuilderPatternPatchCandidate?.CanStage == true;
    public string BuilderPatternLibraryPatchSummary => BuildBuilderPatternPatchOverlaySummary("Approved Pattern Library");
    public string BuilderRecoveryPatternPatchSummary => BuildBuilderPatternPatchOverlaySummary("Recovery Guidance");
    public string BuilderRecoveryComparisonPatternPatchSummary => BuildBuilderPatternPatchOverlaySummary("Compare Recovery Options");
    public string BuilderExternalPatternPatchSummary => BuildBuilderPatternPatchOverlaySummary("External Code Intake");

    public AsyncRelayCommand GenerateBuilderPatternPatchesCommand { get; private set; } = null!;
    public AsyncRelayCommand StageBuilderPatternPatchCandidateCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternPatchArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternPatchExplanationArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternPatchProvenanceArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternPatchMatchArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternPatchCandidateRow> SelectBuilderPatternPatchCandidateCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderPatternPatchArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderPatternPatchSurface()
    {
        BuilderPatternPatchCandidates = new ReadOnlyObservableCollection<BuilderPatternPatchCandidateRow>(_builderPatternPatchCandidates);
        BuilderPatternPatchSelectedTransformationSteps = new ReadOnlyObservableCollection<string>(_builderPatternPatchSelectedTransformationSteps);
        BuilderPatternPatchSelectedSourceElements = new ReadOnlyObservableCollection<string>(_builderPatternPatchSelectedSourceElements);
        BuilderPatternPatchSelectedTargetElements = new ReadOnlyObservableCollection<string>(_builderPatternPatchSelectedTargetElements);
        BuilderPatternPatchSelectedMappingRules = new ReadOnlyObservableCollection<string>(_builderPatternPatchSelectedMappingRules);
        BuilderPatternPatchSelectedArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderPatternPatchSelectedArtifactLinks);
        GenerateBuilderPatternPatchesCommand = new AsyncRelayCommand(GenerateBuilderPatternPatchesAsync, () => CanGenerateBuilderPatternPatches);
        StageBuilderPatternPatchCandidateCommand = new AsyncRelayCommand(StageBuilderPatternPatchCandidateAsync, () => CanStageBuilderPatternPatchCandidate);
        OpenBuilderPatternPatchArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternPatchArtifactPath), () => HasBuilderPatternPatchArtifactPath);
        OpenBuilderPatternPatchExplanationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternPatchExplanationArtifactPath), () => HasBuilderPatternPatchExplanationArtifactPath);
        OpenBuilderPatternPatchProvenanceArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternPatchProvenanceArtifactPath), () => HasBuilderPatternPatchProvenanceArtifactPath);
        OpenBuilderPatternPatchMatchArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternPatchMatchArtifactPath), () => HasBuilderPatternPatchMatchArtifactPath);
        SelectBuilderPatternPatchCandidateCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternPatchCandidateRow>(SelectBuilderPatternPatchCandidateAsync, row => row is not null);
        OpenBuilderPatternPatchArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderPatternPatchArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderPatternPatchArtifacts(BuilderPatternPatchSynthesisContext? context = null)
    {
        context ??= BuilderPatternPatchSynthesisService.LoadPatternPatchContext(GetBuilderWorkspaceRepoRoot());
        if (context is null)
        {
            ResetBuilderPatternPatchState();
            return;
        }

        _builderPatternPatchSummary = context.Patches.Summary;
        _builderPatternPatchArtifactPath = context.Patches.ArtifactPath;
        _builderPatternPatchExplanationArtifactPath = context.Explanations.ArtifactPath;
        _builderPatternPatchProvenanceArtifactPath = context.Provenance.ArtifactPath;
        _builderPatternPatchMatchArtifactPath = context.Matches.ArtifactPath;

        _builderPatternPatchAllCandidates = context.Patches.Candidates
            .Select(candidate => new BuilderPatternPatchCandidateRow(
                candidate,
                context.Explanations.Explanations.FirstOrDefault(entry => string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase)),
                context.Provenance.Entries.FirstOrDefault(entry => string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase)),
                context.Matches.Matches.FirstOrDefault(entry => string.Equals(entry.PatchCandidateId, candidate.PatchCandidateId, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.TargetSummary, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Header, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _builderPatternPatchCandidates.Clear();
        foreach (var row in _builderPatternPatchAllCandidates)
        {
            _builderPatternPatchCandidates.Add(row);
        }

        var selectedId = _selectedBuilderPatternPatchCandidate?.PatchCandidateId;
        ApplySelectedBuilderPatternPatchCandidate(
            _builderPatternPatchAllCandidates.FirstOrDefault(row => string.Equals(row.PatchCandidateId, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _builderPatternPatchAllCandidates.FirstOrDefault());
        NotifyBuilderPatternPatchStateChanged();
    }

    private void ResetBuilderPatternPatchState()
    {
        _builderPatternPatchSummary = "No synthesized pattern patch candidates recorded.";
        _builderPatternPatchArtifactPath = string.Empty;
        _builderPatternPatchExplanationArtifactPath = string.Empty;
        _builderPatternPatchProvenanceArtifactPath = string.Empty;
        _builderPatternPatchMatchArtifactPath = string.Empty;
        _builderPatternPatchAllCandidates = Array.Empty<BuilderPatternPatchCandidateRow>();
        _builderPatternPatchCandidates.Clear();
        ApplySelectedBuilderPatternPatchCandidate(null);
        NotifyBuilderPatternPatchStateChanged();
    }

    private void ApplySelectedBuilderPatternPatchCandidate(BuilderPatternPatchCandidateRow? row)
    {
        _selectedBuilderPatternPatchCandidate = row;
        _builderPatternPatchSelectedTransformationSteps.Clear();
        _builderPatternPatchSelectedSourceElements.Clear();
        _builderPatternPatchSelectedTargetElements.Clear();
        _builderPatternPatchSelectedMappingRules.Clear();
        _builderPatternPatchSelectedArtifactLinks.Clear();
        if (row is null)
        {
            return;
        }

        foreach (var step in row.Explanation?.TransformationSteps ?? Array.Empty<string>())
        {
            _builderPatternPatchSelectedTransformationSteps.Add(step);
        }

        foreach (var sourceElement in row.Explanation?.SourceElements ?? Array.Empty<string>())
        {
            _builderPatternPatchSelectedSourceElements.Add(sourceElement);
        }

        foreach (var targetElement in row.Explanation?.TargetElements ?? Array.Empty<string>())
        {
            _builderPatternPatchSelectedTargetElements.Add(targetElement);
        }

        foreach (var mappingRule in row.Explanation?.MappingRules ?? Array.Empty<string>())
        {
            _builderPatternPatchSelectedMappingRules.Add(mappingRule);
        }

        foreach (var artifact in row.ArtifactLinks)
        {
            _builderPatternPatchSelectedArtifactLinks.Add(artifact);
        }
    }

    private async Task GenerateBuilderPatternPatchesAsync()
    {
        var context = await Task.Run(() => BuilderPatternPatchSynthesisService.RefreshPatternPatchArtifacts(GetBuilderWorkspaceRepoRoot())).ConfigureAwait(true);
        if (context is not null)
        {
            LoadBuilderPatternPatchArtifacts(context);
        }
    }

    private async Task StageBuilderPatternPatchCandidateAsync()
    {
        var candidate = _selectedBuilderPatternPatchCandidate;
        if (candidate is null || !candidate.CanStage)
        {
            return;
        }

        var context = await Task.Run(() => BuilderPatternPatchSynthesisService.StagePatchCandidateForReview(GetBuilderWorkspaceRepoRoot(), candidate.PatchCandidateId)).ConfigureAwait(true);
        if (context is null)
        {
            return;
        }

        LoadBuilderPatternPatchArtifacts();
        LoadBuilderReviewWorkspaceArtifacts();
        RecordBuilderPatternPatchDecision(candidate, context, DateTimeOffset.UtcNow);
    }

    private Task SelectBuilderPatternPatchCandidateAsync(BuilderPatternPatchCandidateRow? row)
    {
        ApplySelectedBuilderPatternPatchCandidate(row);
        NotifyBuilderPatternPatchStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderPatternPatchArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void RecordBuilderPatternPatchDecision(
        BuilderPatternPatchCandidateRow candidate,
        BuilderReviewWorkspaceContext reviewContext,
        DateTimeOffset observedUtc)
    {
        var resultRunId = candidate.Candidate.StagedReviewSessionId;
        RecordBuilderOperatorDecision(
            "stage_pattern_patch_candidate",
            "pattern_patch_stage",
            string.IsNullOrWhiteSpace(resultRunId) ? candidate.PatchCandidateId : resultRunId,
            "partial_success",
            successFlag: true,
            failureClass: string.Empty,
            new[]
            {
                reviewContext.Workspace.ArtifactPath,
                reviewContext.NavigationState.ArtifactPath,
                reviewContext.Queue.ArtifactPath,
                _builderPatternPatchArtifactPath,
                _builderPatternPatchExplanationArtifactPath,
                _builderPatternPatchProvenanceArtifactPath,
                _builderPatternPatchMatchArtifactPath
            },
            observedUtc);
    }

    private string BuildBuilderPatternPatchOverlaySummary(string surfaceName)
    {
        if (_builderPatternPatchAllCandidates.Length == 0)
        {
            return $"{surfaceName}: no synthesized pattern patch candidates recorded.";
        }

        var topCandidate = _builderPatternPatchAllCandidates[0];
        return $"{surfaceName}: {topCandidate.Header} is the current top synthesized candidate. {topCandidate.EligibilitySummary}";
    }

    private void NotifyBuilderPatternPatchStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderPatternPatchCandidates));
        OnPropertyChanged(nameof(BuilderPatternPatchSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternPatchExplanationArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternPatchProvenanceArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternPatchMatchArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternPatchArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternPatchExplanationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternPatchProvenanceArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternPatchMatchArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderPatternPatchCandidate));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedTitle));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedTargetSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedEligibilitySummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedRiskSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedLicenseSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedProvenanceSummary));
        OnPropertyChanged(nameof(BuilderPatternPatchSelectedDiffText));
        OnPropertyChanged(nameof(HasBuilderPatternPatchSelectedTransformationSteps));
        OnPropertyChanged(nameof(HasBuilderPatternPatchSelectedSourceElements));
        OnPropertyChanged(nameof(HasBuilderPatternPatchSelectedTargetElements));
        OnPropertyChanged(nameof(HasBuilderPatternPatchSelectedMappingRules));
        OnPropertyChanged(nameof(HasBuilderPatternPatchSelectedArtifactLinks));
        OnPropertyChanged(nameof(CanGenerateBuilderPatternPatches));
        OnPropertyChanged(nameof(CanStageBuilderPatternPatchCandidate));
        OnPropertyChanged(nameof(BuilderPatternLibraryPatchSummary));
        OnPropertyChanged(nameof(BuilderRecoveryPatternPatchSummary));
        OnPropertyChanged(nameof(BuilderRecoveryComparisonPatternPatchSummary));
        OnPropertyChanged(nameof(BuilderExternalPatternPatchSummary));
        GenerateBuilderPatternPatchesCommand?.RaiseCanExecuteChanged();
        StageBuilderPatternPatchCandidateCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternPatchArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternPatchExplanationArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternPatchProvenanceArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternPatchMatchArtifactCommand?.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderPatternPatchCandidateRow(
    BuilderPatternPatchCandidateRecord Candidate,
    BuilderPatternPatchExplanationRecord? Explanation,
    BuilderPatternPatchProvenanceEntryRecord? Provenance,
    BuilderPatternPatchMatchRecord? Match)
{
    public string PatchCandidateId => Candidate.PatchCandidateId;
    public string Header => $"{Candidate.SynthesisType.Replace('_', ' ')} [{Candidate.PatternEntryId}]";
    public string Summary => Candidate.Summary;
    public string TargetSummary => Candidate.TargetPaths.Count == 0 ? "No target path resolved." : $"Targets: {string.Join(", ", Candidate.TargetPaths)}.";
    public string EligibilitySummary => $"Eligibility: {Candidate.SynthesisEligibility.Replace('_', ' ')}. {Candidate.EligibilityReason}";
    public string RiskSummary => $"Risk: {Candidate.RiskLevel}. Confidence: {Candidate.ConfidenceClass}.";
    public string LicenseSummary => $"License: {Candidate.LicenseStatus.Replace('_', ' ')}. Usage: {Candidate.ApprovedUsageClass.Replace('_', ' ')}.";
    public string ProvenanceSummary => Provenance?.Summary ?? $"Source snapshot: {Candidate.SourceSnapshotId}.";
    public string DiffPreview => string.IsNullOrWhiteSpace(Candidate.DiffText) ? "No diff preview recorded because synthesis is blocked." : Candidate.DiffText;
    public bool CanStage => string.Equals(Candidate.SynthesisEligibility, "ready_for_synthesis", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Candidate.DiffText);
    public int SortOrder => CanStage ? 0 : 1;
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Candidate.ArtifactLinks.Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path)).ToArray();
}
