using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderSignalCalibrationWeightRow> _builderSignalCalibrationWeights = new();
    private BuilderSignalCalibrationRecord? _builderSignalCalibrationArtifact;
    private string _builderSignalCalibrationSummary = "No signal calibration recorded.";
    private string _builderSignalCalibrationProfileSummary = "No signal balance profile recorded.";
    private string _builderSignalCalibrationArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderSignalCalibrationWeightRow> BuilderSignalCalibrationWeights { get; private set; } = null!;
    public bool HasBuilderSignalCalibration => !string.IsNullOrWhiteSpace(_builderSignalCalibrationSummary) &&
                                               !string.Equals(_builderSignalCalibrationSummary, "No signal calibration recorded.", StringComparison.Ordinal);
    public bool HasBuilderSignalCalibrationWeights => _builderSignalCalibrationWeights.Count > 0;
    public string BuilderSignalCalibrationSummary => _builderSignalCalibrationSummary;
    public string BuilderSignalCalibrationProfileSummary => _builderSignalCalibrationProfileSummary;
    public bool HasBuilderSignalCalibrationProfileSummary => !string.IsNullOrWhiteSpace(_builderSignalCalibrationProfileSummary) &&
                                                             !string.Equals(_builderSignalCalibrationProfileSummary, "No signal balance profile recorded.", StringComparison.Ordinal);
    public string BuilderSignalCalibrationArtifactPath => _builderSignalCalibrationArtifactPath;
    public bool HasBuilderSignalCalibrationArtifactPath => !string.IsNullOrWhiteSpace(_builderSignalCalibrationArtifactPath) && File.Exists(_builderSignalCalibrationArtifactPath);

    public AsyncRelayCommand OpenBuilderSignalCalibrationArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderSignalCalibrationSurface()
    {
        BuilderSignalCalibrationWeights = new ReadOnlyObservableCollection<BuilderSignalCalibrationWeightRow>(_builderSignalCalibrationWeights);
        OpenBuilderSignalCalibrationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderSignalCalibrationArtifactPath), () => HasBuilderSignalCalibrationArtifactPath);
    }

    private BuilderSignalCalibrationRecord? LoadBuilderSignalCalibrationArtifacts(
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderOperatorDecisionsRecord? decisionsArtifact = null,
        BuilderPreventativeGuardrailsReport? guardrailArtifact = null)
    {
        var profileArtifact = LoadBuilderSignalProfileArtifacts(decisionsArtifact);
        var artifact = BuilderSignalCalibrationService.RefreshSignalCalibration(
            GetBuilderWorkspaceRepoRoot(),
            rankingArtifact,
            contextFilterArtifact,
            BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot()),
            accuracyArtifact,
            decisionsArtifact,
            BuilderExecutionAuditService.LoadExecutionAudit(GetBuilderWorkspaceRepoRoot()),
            guardrailArtifact,
            BuilderOperatorIntentService.LoadOperatorIntent(GetBuilderWorkspaceRepoRoot()),
            profiles: profileArtifact);
        _builderSignalCalibrationArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderSignalCalibrationState();
            return null;
        }

        _builderSignalCalibrationSummary = artifact.Summary;
        _builderSignalCalibrationProfileSummary = $"Calibration profile {artifact.CalibrationProfile}. Active signal profile {artifact.ActiveProfileName}. Override snapshot {artifact.ProfileOverrideHash}. Dominant weight: {artifact.Weights.OrderByDescending(entry => entry.AdjustedWeight).ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase).First().SignalId} at {artifact.Weights.Max(entry => entry.AdjustedWeight):P0}.";
        _builderSignalCalibrationArtifactPath = artifact.ArtifactPath;

        _builderSignalCalibrationWeights.Clear();
        foreach (var row in artifact.Weights
                     .OrderByDescending(weight => weight.AdjustedWeight)
                     .ThenBy(weight => weight.SignalId, StringComparer.OrdinalIgnoreCase)
                     .Select(weight => new BuilderSignalCalibrationWeightRow(weight)))
        {
            _builderSignalCalibrationWeights.Add(row);
        }

        NotifyBuilderSignalCalibrationStateChanged();
        return artifact;
    }

    private void ResetBuilderSignalCalibrationState()
    {
        _builderSignalCalibrationArtifact = null;
        _builderSignalCalibrationSummary = "No signal calibration recorded.";
        _builderSignalCalibrationProfileSummary = "No signal balance profile recorded.";
        _builderSignalCalibrationArtifactPath = string.Empty;
        _builderSignalCalibrationWeights.Clear();
        ResetBuilderSignalProfileState();
        NotifyBuilderSignalCalibrationStateChanged();
    }

    private void NotifyBuilderSignalCalibrationStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderSignalCalibration));
        OnPropertyChanged(nameof(HasBuilderSignalCalibrationWeights));
        OnPropertyChanged(nameof(BuilderSignalCalibrationSummary));
        OnPropertyChanged(nameof(BuilderSignalCalibrationProfileSummary));
        OnPropertyChanged(nameof(HasBuilderSignalCalibrationProfileSummary));
        OnPropertyChanged(nameof(BuilderSignalCalibrationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderSignalCalibrationArtifactPath));
        OpenBuilderSignalCalibrationArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderSignalCalibrationWeightRow(BuilderSignalCalibrationWeightRecord Weight)
{
    public string Header => BuilderSignalCalibrationService.GetSignalLabel(Weight.SignalId);
    public string ScoreSummary => $"Balanced base {Weight.BaseWeight:P0}; profile base {Weight.ProfileWeight:P0}; context {Weight.ContextAdjustedWeight:P0}; override-applied {Weight.OverrideAdjustedWeight:P0}; final normalized {Weight.AdjustedWeight:P0}.";
    public string Summary => Weight.AdjustmentReason;
}
