using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderSignalProfileOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderSignalOverrideOptions = new();
    private BuilderSignalProfilesRecord? _builderSignalProfilesArtifact;
    private string _builderSignalProfileSummary = "No signal profile artifact recorded.";
    private string _builderSignalOverrideBoundsSummary = "No bounded override bounds recorded.";
    private string _builderSignalProfileArtifactPath = string.Empty;
    private string _builderSignalProfileChangedSinceDecisionSummary = string.Empty;
    private string _selectedBuilderSignalProfileId = BuilderSignalProfileService.BalancedDefaultProfileId;
    private string _selectedBuilderSignalRankingOverride = "0";
    private string _selectedBuilderSignalIntentOverride = "0";
    private string _selectedBuilderSignalConstraintOverride = "0";
    private string _selectedBuilderSignalTrustOverride = "0";
    private string _selectedBuilderSignalGuardrailOverride = "0";
    private string _selectedBuilderSignalDriftOverride = "0";
    private bool _isHydratingBuilderSignalProfileSelection;

    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderSignalProfileOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderSignalOverrideOptions { get; private set; } = null!;

    public string BuilderSignalProfileSummary => _builderSignalProfileSummary;
    public bool HasBuilderSignalProfileSummary => !string.IsNullOrWhiteSpace(_builderSignalProfileSummary) &&
                                                  !string.Equals(_builderSignalProfileSummary, "No signal profile artifact recorded.", StringComparison.Ordinal);
    public string BuilderSignalOverrideBoundsSummary => _builderSignalOverrideBoundsSummary;
    public bool HasBuilderSignalOverrideBoundsSummary => !string.IsNullOrWhiteSpace(_builderSignalOverrideBoundsSummary) &&
                                                         !string.Equals(_builderSignalOverrideBoundsSummary, "No bounded override bounds recorded.", StringComparison.Ordinal);
    public string BuilderSignalProfileArtifactPath => _builderSignalProfileArtifactPath;
    public bool HasBuilderSignalProfileArtifactPath => !string.IsNullOrWhiteSpace(_builderSignalProfileArtifactPath) && File.Exists(_builderSignalProfileArtifactPath);
    public string BuilderSignalProfileChangedSinceDecisionSummary => _builderSignalProfileChangedSinceDecisionSummary;
    public bool HasBuilderSignalProfileChangedSinceDecisionSummary => !string.IsNullOrWhiteSpace(_builderSignalProfileChangedSinceDecisionSummary);

    public string SelectedBuilderSignalProfileId
    {
        get => _selectedBuilderSignalProfileId;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedBuilderSignalProfileId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderSignalProfileId = normalized;
            OnPropertyChanged(nameof(SelectedBuilderSignalProfileId));
            if (!_isHydratingBuilderSignalProfileSelection)
            {
                ApplyBuilderSignalProfileSelection();
            }
        }
    }

    public string SelectedBuilderSignalRankingOverride
    {
        get => _selectedBuilderSignalRankingOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalRankingOverride, value, nameof(SelectedBuilderSignalRankingOverride));
    }

    public string SelectedBuilderSignalIntentOverride
    {
        get => _selectedBuilderSignalIntentOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalIntentOverride, value, nameof(SelectedBuilderSignalIntentOverride));
    }

    public string SelectedBuilderSignalConstraintOverride
    {
        get => _selectedBuilderSignalConstraintOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalConstraintOverride, value, nameof(SelectedBuilderSignalConstraintOverride));
    }

    public string SelectedBuilderSignalTrustOverride
    {
        get => _selectedBuilderSignalTrustOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalTrustOverride, value, nameof(SelectedBuilderSignalTrustOverride));
    }

    public string SelectedBuilderSignalGuardrailOverride
    {
        get => _selectedBuilderSignalGuardrailOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalGuardrailOverride, value, nameof(SelectedBuilderSignalGuardrailOverride));
    }

    public string SelectedBuilderSignalDriftOverride
    {
        get => _selectedBuilderSignalDriftOverride;
        set => SetBuilderSignalOverride(ref _selectedBuilderSignalDriftOverride, value, nameof(SelectedBuilderSignalDriftOverride));
    }

    public AsyncRelayCommand OpenBuilderSignalProfileArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand ResetBuilderSignalOverridesCommand { get; private set; } = null!;

    private void InitializeBuilderSignalProfileSurface()
    {
        BuilderSignalProfileOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderSignalProfileOptions);
        BuilderSignalOverrideOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderSignalOverrideOptions);
        PopulateBuilderSignalOverrideOptions();
        OpenBuilderSignalProfileArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderSignalProfileArtifactPath), () => HasBuilderSignalProfileArtifactPath);
        ResetBuilderSignalOverridesCommand = new AsyncRelayCommand(ResetBuilderSignalOverridesAsync, () => _builderSignalProfilesArtifact is not null);
    }

    private BuilderSignalProfilesRecord? LoadBuilderSignalProfileArtifacts(BuilderOperatorDecisionsRecord? decisionsArtifact = null)
    {
        var artifact = BuilderSignalProfileService.LoadSignalProfiles(GetBuilderWorkspaceRepoRoot()) ??
                       BuilderSignalProfileService.RefreshSignalProfiles(GetBuilderWorkspaceRepoRoot());
        _builderSignalProfilesArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderSignalProfileState();
            return null;
        }

        decisionsArtifact ??= BuilderOperatorDecisionService.LoadOperatorDecisions(GetBuilderWorkspaceRepoRoot());
        _builderSignalProfileSummary = artifact.Summary;
        _builderSignalOverrideBoundsSummary = BuildBuilderSignalOverrideBoundsSummary(artifact);
        _builderSignalProfileArtifactPath = artifact.ArtifactPath;
        _builderSignalProfileChangedSinceDecisionSummary = BuildBuilderSignalProfileChangedSinceDecisionSummary(artifact, decisionsArtifact);

        _isHydratingBuilderSignalProfileSelection = true;
        try
        {
            PopulateBuilderSignalProfileOptions(artifact);
            _selectedBuilderSignalProfileId = artifact.ActiveProfileId;
            _selectedBuilderSignalRankingOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.RankingSignalId));
            _selectedBuilderSignalIntentOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.IntentSignalId));
            _selectedBuilderSignalConstraintOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.ConstraintSignalId));
            _selectedBuilderSignalTrustOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.TrustSignalId));
            _selectedBuilderSignalGuardrailOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.GuardrailSignalId));
            _selectedBuilderSignalDriftOverride = FormatOverrideValue(BuilderSignalProfileService.ResolveOverrideDelta(artifact, BuilderSignalCalibrationService.DriftSignalId));
        }
        finally
        {
            _isHydratingBuilderSignalProfileSelection = false;
        }

        NotifyBuilderSignalProfileStateChanged();
        return artifact;
    }

    private void ResetBuilderSignalProfileState()
    {
        _builderSignalProfilesArtifact = null;
        _builderSignalProfileSummary = "No signal profile artifact recorded.";
        _builderSignalOverrideBoundsSummary = "No bounded override bounds recorded.";
        _builderSignalProfileArtifactPath = string.Empty;
        _builderSignalProfileChangedSinceDecisionSummary = string.Empty;
        _builderSignalProfileOptions.Clear();
        PopulateBuilderSignalOverrideOptions();
        _selectedBuilderSignalProfileId = BuilderSignalProfileService.BalancedDefaultProfileId;
        _selectedBuilderSignalRankingOverride = "0";
        _selectedBuilderSignalIntentOverride = "0";
        _selectedBuilderSignalConstraintOverride = "0";
        _selectedBuilderSignalTrustOverride = "0";
        _selectedBuilderSignalGuardrailOverride = "0";
        _selectedBuilderSignalDriftOverride = "0";
        NotifyBuilderSignalProfileStateChanged();
    }

    private void ApplyBuilderSignalProfileSelection()
    {
        if (string.IsNullOrWhiteSpace(_selectedBuilderSignalProfileId))
        {
            return;
        }

        BuilderSignalProfileService.SetActiveProfile(GetBuilderWorkspaceRepoRoot(), _selectedBuilderSignalProfileId);
        LoadBuilderSignalProfileArtifacts();
        RefreshBuilderRecoveryRankingState();
    }

    private void SetBuilderSignalOverride(ref string field, string value, string propertyName)
    {
        var normalized = NormalizeOverrideValue(value);
        if (string.Equals(field, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        field = normalized;
        OnPropertyChanged(propertyName);
        if (!_isHydratingBuilderSignalProfileSelection)
        {
            ApplyBuilderSignalOverrides();
        }
    }

    private void ApplyBuilderSignalOverrides()
    {
        var overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [BuilderSignalCalibrationService.RankingSignalId] = ParseOverrideValue(_selectedBuilderSignalRankingOverride),
            [BuilderSignalCalibrationService.IntentSignalId] = ParseOverrideValue(_selectedBuilderSignalIntentOverride),
            [BuilderSignalCalibrationService.ConstraintSignalId] = ParseOverrideValue(_selectedBuilderSignalConstraintOverride),
            [BuilderSignalCalibrationService.TrustSignalId] = ParseOverrideValue(_selectedBuilderSignalTrustOverride),
            [BuilderSignalCalibrationService.GuardrailSignalId] = ParseOverrideValue(_selectedBuilderSignalGuardrailOverride),
            [BuilderSignalCalibrationService.DriftSignalId] = ParseOverrideValue(_selectedBuilderSignalDriftOverride)
        };
        BuilderSignalProfileService.SaveOverrides(GetBuilderWorkspaceRepoRoot(), overrides);
        LoadBuilderSignalProfileArtifacts();
        RefreshBuilderRecoveryRankingState();
    }

    private Task ResetBuilderSignalOverridesAsync()
    {
        BuilderSignalProfileService.ResetOverrides(GetBuilderWorkspaceRepoRoot());
        LoadBuilderSignalProfileArtifacts();
        RefreshBuilderRecoveryRankingState();
        return Task.CompletedTask;
    }

    private void PopulateBuilderSignalProfileOptions(BuilderSignalProfilesRecord artifact)
    {
        _builderSignalProfileOptions.Clear();
        foreach (var profile in artifact.Profiles
                     .OrderBy(profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            _builderSignalProfileOptions.Add(new BuilderRecoveryOptionRow(profile.ProfileId, profile.ProfileName));
        }
    }

    private void PopulateBuilderSignalOverrideOptions()
    {
        _builderSignalOverrideOptions.Clear();
        foreach (var value in new[] { -0.06d, -0.03d, 0d, 0.03d, 0.06d })
        {
            _builderSignalOverrideOptions.Add(new BuilderRecoveryOptionRow(FormatOverrideValue(value), BuilderSignalProfileService.FormatOverrideLabel(value)));
        }
    }

    private string BuildBuilderSignalOverrideBoundsSummary(BuilderSignalProfilesRecord artifact)
    {
        var activeProfile = BuilderSignalProfileService.ResolveActiveProfile(artifact);
        var activeOverrides = artifact.ActiveOverrides.Count(entry => Math.Abs(entry.AppliedDelta) > 0.0001d);
        var minGuardrail = artifact.OverridePolicy.MinWeightPerSignal.FirstOrDefault(entry =>
            string.Equals(entry.SignalId, BuilderSignalCalibrationService.GuardrailSignalId, StringComparison.OrdinalIgnoreCase))?.Weight ?? 0d;
        var maxRanking = artifact.OverridePolicy.MaxWeightPerSignal.FirstOrDefault(entry =>
            string.Equals(entry.SignalId, BuilderSignalCalibrationService.RankingSignalId, StringComparison.OrdinalIgnoreCase))?.Weight ?? 0d;
        return $"Active profile {activeProfile.ProfileName}. Override bounds follow {artifact.OverridePolicy.NormalizationRule}. Active bounded overrides: {activeOverrides}. Guardrail minimum {minGuardrail:P0}. Ranking maximum {maxRanking:P0}.";
    }

    private static string BuildBuilderSignalProfileChangedSinceDecisionSummary(
        BuilderSignalProfilesRecord artifact,
        BuilderOperatorDecisionsRecord? decisions)
    {
        var latestDecision = (decisions?.Decisions ?? Array.Empty<BuilderOperatorDecisionRecord>())
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.DecisionId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latestDecision is null)
        {
            return "No operator decision has been recorded under the current signal profile yet.";
        }

        var currentHash = BuilderSignalProfileService.ResolveOverrideHash(artifact);
        if (string.Equals(latestDecision.ActiveSignalProfileId, artifact.ActiveProfileId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(latestDecision.ProfileOverrideHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"Signal profile changed since the latest operator decision. Current: {BuilderSignalProfileService.GetProfileLabel(artifact.ActiveProfileId)} ({currentHash}). Last decision used {BuilderSignalProfileService.GetProfileLabel(latestDecision.ActiveSignalProfileId)} ({latestDecision.ProfileOverrideHash}).";
    }

    private static string NormalizeOverrideValue(string? value)
    {
        var parsed = ParseOverrideValue(value);
        return FormatOverrideValue(parsed);
    }

    private static double ParseOverrideValue(string? value)
        => double.TryParse(value, out var parsed) ? parsed : 0d;

    private static string FormatOverrideValue(double value)
        => Math.Round(value, 4).ToString("0.####");

    private void NotifyBuilderSignalProfileStateChanged()
    {
        OnPropertyChanged(nameof(BuilderSignalProfileSummary));
        OnPropertyChanged(nameof(HasBuilderSignalProfileSummary));
        OnPropertyChanged(nameof(BuilderSignalOverrideBoundsSummary));
        OnPropertyChanged(nameof(HasBuilderSignalOverrideBoundsSummary));
        OnPropertyChanged(nameof(BuilderSignalProfileArtifactPath));
        OnPropertyChanged(nameof(HasBuilderSignalProfileArtifactPath));
        OnPropertyChanged(nameof(BuilderSignalProfileChangedSinceDecisionSummary));
        OnPropertyChanged(nameof(HasBuilderSignalProfileChangedSinceDecisionSummary));
        OnPropertyChanged(nameof(SelectedBuilderSignalProfileId));
        OnPropertyChanged(nameof(SelectedBuilderSignalRankingOverride));
        OnPropertyChanged(nameof(SelectedBuilderSignalIntentOverride));
        OnPropertyChanged(nameof(SelectedBuilderSignalConstraintOverride));
        OnPropertyChanged(nameof(SelectedBuilderSignalTrustOverride));
        OnPropertyChanged(nameof(SelectedBuilderSignalGuardrailOverride));
        OnPropertyChanged(nameof(SelectedBuilderSignalDriftOverride));
        OpenBuilderSignalProfileArtifactCommand.RaiseCanExecuteChanged();
        ResetBuilderSignalOverridesCommand.RaiseCanExecuteChanged();
    }
}
