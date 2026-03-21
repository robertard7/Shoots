using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderExternalReconModeOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderExternalSourceKindOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderExternalIntakeModeOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderExternalSnapshotOptions = new();
    private readonly ObservableCollection<BuilderExternalSuggestionRow> _builderExternalSuggestions = new();
    private readonly ObservableCollection<BuilderExternalSnapshotRow> _builderExternalSnapshots = new();
    private readonly ObservableCollection<BuilderExternalEvaluationRow> _builderExternalEvaluations = new();
    private readonly ObservableCollection<BuilderExternalVendorCandidateRow> _builderExternalVendorCandidates = new();
    private readonly ObservableCollection<BuilderExternalProvenanceRow> _builderExternalProvenanceEntries = new();
    private string _builderExternalReconSummary = "External recon is off. Normal builder operation remains local and unchanged.";
    private string _builderExternalModeSummary = "Recon mode Off keeps the builder local and network-free.";
    private string _builderExternalDisabledReason = "External recon is off, so manual URL intake stays inactive.";
    private string _builderExternalLatestMetadataSummary = "No external metadata intake recorded.";
    private string _builderExternalLatestSnapshotSummary = "No external snapshot recorded.";
    private string _builderExternalLatestEvaluationSummary = "No external evaluation recorded.";
    private string _builderExternalLatestVendorCandidateSummary = "No vendor candidate recorded.";
    private string _builderExternalProvenanceSummary = "No external provenance entries recorded.";
    private string _builderExternalReconArtifactPath = string.Empty;
    private string _builderExternalSnapshotArtifactPath = string.Empty;
    private string _builderExternalEvaluationArtifactPath = string.Empty;
    private string _builderExternalVendorCandidateArtifactPath = string.Empty;
    private string _builderExternalProvenanceArtifactPath = string.Empty;
    private string _selectedBuilderExternalReconMode = BuilderExternalReconService.ReconModeOff;
    private string _selectedBuilderExternalSourceKind = BuilderExternalReconService.SourceKindRepo;
    private string _selectedBuilderExternalIntakeMode = BuilderExternalReconService.IntakeModeMetadataOnly;
    private string _selectedBuilderExternalSnapshotId = string.Empty;
    private string _builderExternalSourceUrl = string.Empty;
    private string _builderExternalRequestedRef = string.Empty;
    private string _builderExternalOperatorNote = string.Empty;
    private bool _isHydratingBuilderExternalRecon;

    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderExternalReconModeOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderExternalSourceKindOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderExternalIntakeModeOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderExternalSnapshotOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExternalSuggestionRow> BuilderExternalSuggestions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExternalSnapshotRow> BuilderExternalSnapshots { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExternalEvaluationRow> BuilderExternalEvaluations { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExternalVendorCandidateRow> BuilderExternalVendorCandidates { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExternalProvenanceRow> BuilderExternalProvenanceEntries { get; private set; } = null!;

    public string BuilderExternalReconSummary => _builderExternalReconSummary;
    public string BuilderExternalModeSummary => _builderExternalModeSummary;
    public string BuilderExternalDisabledReason => _builderExternalDisabledReason;
    public string BuilderExternalLatestMetadataSummary => _builderExternalLatestMetadataSummary;
    public string BuilderExternalLatestSnapshotSummary => _builderExternalLatestSnapshotSummary;
    public string BuilderExternalLatestEvaluationSummary => _builderExternalLatestEvaluationSummary;
    public string BuilderExternalLatestVendorCandidateSummary => _builderExternalLatestVendorCandidateSummary;
    public string BuilderExternalProvenanceSummary => _builderExternalProvenanceSummary;
    public string BuilderExternalReconArtifactPath => _builderExternalReconArtifactPath;
    public string BuilderExternalSnapshotArtifactPath => _builderExternalSnapshotArtifactPath;
    public string BuilderExternalEvaluationArtifactPath => _builderExternalEvaluationArtifactPath;
    public string BuilderExternalVendorCandidateArtifactPath => _builderExternalVendorCandidateArtifactPath;
    public string BuilderExternalProvenanceArtifactPath => _builderExternalProvenanceArtifactPath;
    public bool HasBuilderExternalSuggestions => _builderExternalSuggestions.Count > 0;
    public bool HasBuilderExternalSnapshots => _builderExternalSnapshots.Count > 0;
    public bool HasBuilderExternalEvaluations => _builderExternalEvaluations.Count > 0;
    public bool HasBuilderExternalVendorCandidates => _builderExternalVendorCandidates.Count > 0;
    public bool HasBuilderExternalProvenanceEntries => _builderExternalProvenanceEntries.Count > 0;
    public bool CanBuilderExternalManualIntake => BuilderExternalReconService.ModeAllowsManualIntake(_selectedBuilderExternalReconMode);
    public bool CanBuilderExternalSuggestions => BuilderExternalReconService.ModeAllowsSuggestions(_selectedBuilderExternalReconMode);
    public bool HasBuilderExternalReconArtifactPath => !string.IsNullOrWhiteSpace(_builderExternalReconArtifactPath) && File.Exists(_builderExternalReconArtifactPath);
    public bool HasBuilderExternalSnapshotArtifactPath => !string.IsNullOrWhiteSpace(_builderExternalSnapshotArtifactPath) && File.Exists(_builderExternalSnapshotArtifactPath);
    public bool HasBuilderExternalEvaluationArtifactPath => !string.IsNullOrWhiteSpace(_builderExternalEvaluationArtifactPath) && File.Exists(_builderExternalEvaluationArtifactPath);
    public bool HasBuilderExternalVendorCandidateArtifactPath => !string.IsNullOrWhiteSpace(_builderExternalVendorCandidateArtifactPath) && File.Exists(_builderExternalVendorCandidateArtifactPath);
    public bool HasBuilderExternalProvenanceArtifactPath => !string.IsNullOrWhiteSpace(_builderExternalProvenanceArtifactPath) && File.Exists(_builderExternalProvenanceArtifactPath);

    public string SelectedBuilderExternalReconMode
    {
        get => _selectedBuilderExternalReconMode;
        set
        {
            var normalized = BuilderExternalReconService.NormalizeReconMode(value);
            if (string.Equals(_selectedBuilderExternalReconMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderExternalReconMode = normalized;
            OnPropertyChanged(nameof(SelectedBuilderExternalReconMode));
            if (!_isHydratingBuilderExternalRecon)
            {
                ApplyBuilderExternalReconModeSelection();
            }
        }
    }

    public string SelectedBuilderExternalSourceKind
    {
        get => _selectedBuilderExternalSourceKind;
        set
        {
            if (string.Equals(_selectedBuilderExternalSourceKind, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderExternalSourceKind = value?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(SelectedBuilderExternalSourceKind));
        }
    }

    public string SelectedBuilderExternalIntakeMode
    {
        get => _selectedBuilderExternalIntakeMode;
        set
        {
            if (string.Equals(_selectedBuilderExternalIntakeMode, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderExternalIntakeMode = value?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(SelectedBuilderExternalIntakeMode));
        }
    }

    public string SelectedBuilderExternalSnapshotId
    {
        get => _selectedBuilderExternalSnapshotId;
        set
        {
            if (string.Equals(_selectedBuilderExternalSnapshotId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderExternalSnapshotId = value?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(SelectedBuilderExternalSnapshotId));
            NotifyBuilderExternalReconStateChanged();
            NotifyBuilderPatternLibraryStateChanged();
        }
    }

    public string BuilderExternalSourceUrl
    {
        get => _builderExternalSourceUrl;
        set
        {
            if (string.Equals(_builderExternalSourceUrl, value, StringComparison.Ordinal))
            {
                return;
            }

            _builderExternalSourceUrl = value ?? string.Empty;
            OnPropertyChanged(nameof(BuilderExternalSourceUrl));
            NotifyBuilderExternalReconStateChanged();
        }
    }

    public string BuilderExternalRequestedRef
    {
        get => _builderExternalRequestedRef;
        set
        {
            if (string.Equals(_builderExternalRequestedRef, value, StringComparison.Ordinal))
            {
                return;
            }

            _builderExternalRequestedRef = value ?? string.Empty;
            OnPropertyChanged(nameof(BuilderExternalRequestedRef));
        }
    }

    public string BuilderExternalOperatorNote
    {
        get => _builderExternalOperatorNote;
        set
        {
            if (string.Equals(_builderExternalOperatorNote, value, StringComparison.Ordinal))
            {
                return;
            }

            _builderExternalOperatorNote = value ?? string.Empty;
            OnPropertyChanged(nameof(BuilderExternalOperatorNote));
        }
    }

    public AsyncRelayCommand OpenBuilderExternalReconArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderExternalSnapshotArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderExternalEvaluationArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderExternalVendorCandidateArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderExternalProvenanceArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand FetchBuilderExternalMetadataCommand { get; private set; } = null!;
    public AsyncRelayCommand SnapshotBuilderExternalSourceCommand { get; private set; } = null!;
    public AsyncRelayCommand EvaluateBuilderExternalSnapshotCommand { get; private set; } = null!;
    public AsyncRelayCommand StageBuilderExternalVendorCandidateCommand { get; private set; } = null!;

    private void InitializeBuilderExternalReconSurface()
    {
        BuilderExternalReconModeOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderExternalReconModeOptions);
        BuilderExternalSourceKindOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderExternalSourceKindOptions);
        BuilderExternalIntakeModeOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderExternalIntakeModeOptions);
        BuilderExternalSnapshotOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderExternalSnapshotOptions);
        BuilderExternalSuggestions = new ReadOnlyObservableCollection<BuilderExternalSuggestionRow>(_builderExternalSuggestions);
        BuilderExternalSnapshots = new ReadOnlyObservableCollection<BuilderExternalSnapshotRow>(_builderExternalSnapshots);
        BuilderExternalEvaluations = new ReadOnlyObservableCollection<BuilderExternalEvaluationRow>(_builderExternalEvaluations);
        BuilderExternalVendorCandidates = new ReadOnlyObservableCollection<BuilderExternalVendorCandidateRow>(_builderExternalVendorCandidates);
        BuilderExternalProvenanceEntries = new ReadOnlyObservableCollection<BuilderExternalProvenanceRow>(_builderExternalProvenanceEntries);
        PopulateBuilderExternalReconOptions();
        OpenBuilderExternalReconArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExternalReconArtifactPath), () => HasBuilderExternalReconArtifactPath);
        OpenBuilderExternalSnapshotArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExternalSnapshotArtifactPath), () => HasBuilderExternalSnapshotArtifactPath);
        OpenBuilderExternalEvaluationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExternalEvaluationArtifactPath), () => HasBuilderExternalEvaluationArtifactPath);
        OpenBuilderExternalVendorCandidateArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExternalVendorCandidateArtifactPath), () => HasBuilderExternalVendorCandidateArtifactPath);
        OpenBuilderExternalProvenanceArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExternalProvenanceArtifactPath), () => HasBuilderExternalProvenanceArtifactPath);
        FetchBuilderExternalMetadataCommand = new AsyncRelayCommand(FetchBuilderExternalMetadataAsync, () => CanBuilderExternalManualIntake && !string.IsNullOrWhiteSpace(_builderExternalSourceUrl));
        SnapshotBuilderExternalSourceCommand = new AsyncRelayCommand(SnapshotBuilderExternalSourceAsync, () => CanBuilderExternalManualIntake && !string.IsNullOrWhiteSpace(_builderExternalSourceUrl));
        EvaluateBuilderExternalSnapshotCommand = new AsyncRelayCommand(EvaluateBuilderExternalSnapshotAsync, () => CanBuilderExternalManualIntake && !string.IsNullOrWhiteSpace(_selectedBuilderExternalSnapshotId));
        StageBuilderExternalVendorCandidateCommand = new AsyncRelayCommand(StageBuilderExternalVendorCandidateAsync, () => CanBuilderExternalManualIntake && !string.IsNullOrWhiteSpace(_selectedBuilderExternalSnapshotId));
    }

    private void LoadBuilderExternalReconArtifacts()
    {
        var repoRoot = GetBuilderWorkspaceRepoRoot();
        var reconArtifact = BuilderExternalReconService.LoadExternalRecon(repoRoot);
        var snapshotArtifact = BuilderExternalReconService.LoadExternalSourceSnapshots(repoRoot);
        var evaluationArtifact = BuilderExternalReconService.LoadExternalCodeEvaluations(repoRoot);
        var vendorArtifact = BuilderExternalReconService.LoadVendorCandidates(repoRoot);
        var provenanceArtifact = BuilderExternalReconService.LoadExternalProvenanceIndex(repoRoot);

        _isHydratingBuilderExternalRecon = true;
        try
        {
            _selectedBuilderExternalReconMode = reconArtifact?.ReconMode ?? BuilderExternalReconService.ReconModeOff;
        }
        finally
        {
            _isHydratingBuilderExternalRecon = false;
        }

        _builderExternalReconSummary = reconArtifact?.Summary ?? "External recon is off. Normal builder operation remains local and unchanged.";
        _builderExternalModeSummary = BuildBuilderExternalModeSummary(_selectedBuilderExternalReconMode);
        _builderExternalDisabledReason = BuildBuilderExternalDisabledReason(_selectedBuilderExternalReconMode);
        _builderExternalReconArtifactPath = reconArtifact?.ArtifactPath ?? string.Empty;
        _builderExternalSnapshotArtifactPath = snapshotArtifact?.ArtifactPath ?? string.Empty;
        _builderExternalEvaluationArtifactPath = evaluationArtifact?.ArtifactPath ?? string.Empty;
        _builderExternalVendorCandidateArtifactPath = vendorArtifact?.ArtifactPath ?? string.Empty;
        _builderExternalProvenanceArtifactPath = provenanceArtifact?.ArtifactPath ?? string.Empty;

        _builderExternalSuggestions.Clear();
        foreach (var row in (reconArtifact?.Suggestions ?? Array.Empty<BuilderExternalSourceSuggestionRecord>())
                     .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.SuggestionId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderExternalSuggestionRow(entry)))
        {
            _builderExternalSuggestions.Add(row);
        }

        _builderExternalSnapshots.Clear();
        foreach (var row in (snapshotArtifact?.Snapshots ?? Array.Empty<BuilderExternalSourceSnapshotRecord>())
                     .OrderByDescending(entry => entry.ObservedUtc)
                     .ThenBy(entry => entry.SnapshotId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderExternalSnapshotRow(entry)))
        {
            _builderExternalSnapshots.Add(row);
        }

        _builderExternalEvaluations.Clear();
        foreach (var row in (evaluationArtifact?.Evaluations ?? Array.Empty<BuilderExternalCodeEvaluationRecord>())
                     .OrderByDescending(entry => entry.ObservedUtc)
                     .ThenBy(entry => entry.EvaluationId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderExternalEvaluationRow(entry)))
        {
            _builderExternalEvaluations.Add(row);
        }

        _builderExternalVendorCandidates.Clear();
        foreach (var row in (vendorArtifact?.Candidates ?? Array.Empty<BuilderVendorCandidateRecord>())
                     .OrderByDescending(entry => entry.ObservedUtc)
                     .ThenBy(entry => entry.CandidateId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderExternalVendorCandidateRow(entry)))
        {
            _builderExternalVendorCandidates.Add(row);
        }

        _builderExternalProvenanceEntries.Clear();
        foreach (var row in (provenanceArtifact?.Entries ?? Array.Empty<BuilderExternalProvenanceEntryRecord>())
                     .OrderByDescending(entry => entry.ObservedUtc)
                     .ThenBy(entry => entry.ProvenanceId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderExternalProvenanceRow(entry)))
        {
            _builderExternalProvenanceEntries.Add(row);
        }

        _builderExternalSnapshotOptions.Clear();
        foreach (var option in _builderExternalSnapshots
                     .OrderByDescending(entry => entry.Snapshot.ObservedUtc)
                     .ThenBy(entry => entry.SnapshotId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderRecoveryOptionRow(entry.SnapshotId, entry.Header)))
        {
            _builderExternalSnapshotOptions.Add(option);
        }

        if (!_builderExternalSnapshotOptions.Any(option => string.Equals(option.Value, _selectedBuilderExternalSnapshotId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedBuilderExternalSnapshotId = _builderExternalSnapshotOptions.FirstOrDefault()?.Value ?? string.Empty;
        }

        _builderExternalLatestMetadataSummary = reconArtifact?.Entries
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Summary ?? "No external metadata intake recorded.";
        _builderExternalLatestSnapshotSummary = _builderExternalSnapshots.FirstOrDefault()?.Summary ?? "No external snapshot recorded.";
        _builderExternalLatestEvaluationSummary = _builderExternalEvaluations.FirstOrDefault()?.Summary ?? "No external evaluation recorded.";
        _builderExternalLatestVendorCandidateSummary = _builderExternalVendorCandidates.FirstOrDefault()?.Summary ?? "No vendor candidate recorded.";
        _builderExternalProvenanceSummary = provenanceArtifact?.Summary ?? "No external provenance entries recorded.";
        NotifyBuilderExternalReconStateChanged();
    }

    private void ResetBuilderExternalReconState()
    {
        _builderExternalReconSummary = "External recon is off. Normal builder operation remains local and unchanged.";
        _builderExternalModeSummary = "Recon mode Off keeps the builder local and network-free.";
        _builderExternalDisabledReason = "External recon is off, so manual URL intake stays inactive.";
        _builderExternalLatestMetadataSummary = "No external metadata intake recorded.";
        _builderExternalLatestSnapshotSummary = "No external snapshot recorded.";
        _builderExternalLatestEvaluationSummary = "No external evaluation recorded.";
        _builderExternalLatestVendorCandidateSummary = "No vendor candidate recorded.";
        _builderExternalProvenanceSummary = "No external provenance entries recorded.";
        _builderExternalReconArtifactPath = string.Empty;
        _builderExternalSnapshotArtifactPath = string.Empty;
        _builderExternalEvaluationArtifactPath = string.Empty;
        _builderExternalVendorCandidateArtifactPath = string.Empty;
        _builderExternalProvenanceArtifactPath = string.Empty;
        _selectedBuilderExternalReconMode = BuilderExternalReconService.ReconModeOff;
        _selectedBuilderExternalSnapshotId = string.Empty;
        _builderExternalSuggestions.Clear();
        _builderExternalSnapshots.Clear();
        _builderExternalEvaluations.Clear();
        _builderExternalVendorCandidates.Clear();
        _builderExternalProvenanceEntries.Clear();
        _builderExternalSnapshotOptions.Clear();
        NotifyBuilderExternalReconStateChanged();
    }

    private void ApplyBuilderExternalReconModeSelection()
    {
        BuilderExternalReconService.SetReconMode(GetBuilderWorkspaceRepoRoot(), _selectedBuilderExternalReconMode);
        LoadBuilderExternalReconArtifacts();
    }

    private async Task FetchBuilderExternalMetadataAsync()
    {
        var request = BuildBuilderExternalIntakeRequest();
        await Task.Run(() => BuilderExternalReconService.RecordMetadataDiscovery(GetBuilderWorkspaceRepoRoot(), request)).ConfigureAwait(true);
        LoadBuilderExternalReconArtifacts();
    }

    private async Task SnapshotBuilderExternalSourceAsync()
    {
        var request = BuildBuilderExternalIntakeRequest();
        await Task.Run(() => BuilderExternalReconService.CreateSnapshot(GetBuilderWorkspaceRepoRoot(), request)).ConfigureAwait(true);
        LoadBuilderExternalReconArtifacts();
    }

    private async Task EvaluateBuilderExternalSnapshotAsync()
    {
        var snapshotId = _selectedBuilderExternalSnapshotId;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        await Task.Run(() => BuilderExternalReconService.EvaluateSnapshot(GetBuilderWorkspaceRepoRoot(), snapshotId)).ConfigureAwait(true);
        LoadBuilderExternalReconArtifacts();
    }

    private async Task StageBuilderExternalVendorCandidateAsync()
    {
        var snapshotId = _selectedBuilderExternalSnapshotId;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        await Task.Run(() => BuilderExternalReconService.StageVendorCandidate(GetBuilderWorkspaceRepoRoot(), snapshotId)).ConfigureAwait(true);
        LoadBuilderExternalReconArtifacts();
    }

    private BuilderExternalIntakeRequest BuildBuilderExternalIntakeRequest()
        => new(
            _builderExternalSourceUrl,
            _selectedBuilderExternalSourceKind,
            _builderExternalRequestedRef,
            _selectedBuilderExternalIntakeMode,
            _builderExternalOperatorNote);

    private static string BuildBuilderExternalModeSummary(string reconMode)
        => BuilderExternalReconService.NormalizeReconMode(reconMode) switch
        {
            BuilderExternalReconService.ReconModeManualOnly => "Recon mode Manual Only allows operator-pasted URLs while keeping suggestions hidden.",
            BuilderExternalReconService.ReconModeSuggestOnly => "Recon mode Suggest Only surfaces advisory source ideas without enabling the manual URL lane.",
            BuilderExternalReconService.ReconModeEnabled => "Recon mode Enabled supports both manual URL intake and advisory source suggestions.",
            _ => "Recon mode Off keeps the builder local and network-free."
        };

    private static string BuildBuilderExternalDisabledReason(string reconMode)
        => BuilderExternalReconService.NormalizeReconMode(reconMode) switch
        {
            BuilderExternalReconService.ReconModeSuggestOnly => "Suggest Only keeps the URL lane inactive while still showing advisory source ideas.",
            BuilderExternalReconService.ReconModeOff => "External recon is off, so manual URL intake stays inactive.",
            _ => "Manual external intake is available."
        };

    private void PopulateBuilderExternalReconOptions()
    {
        _builderExternalReconModeOptions.Clear();
        _builderExternalReconModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.ReconModeOff, "Off"));
        _builderExternalReconModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.ReconModeManualOnly, "Manual Only"));
        _builderExternalReconModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.ReconModeSuggestOnly, "Suggest Only"));
        _builderExternalReconModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.ReconModeEnabled, "Enabled"));

        _builderExternalSourceKindOptions.Clear();
        _builderExternalSourceKindOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.SourceKindRepo, "Repository"));
        _builderExternalSourceKindOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.SourceKindFile, "Source File"));
        _builderExternalSourceKindOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.SourceKindArchive, "Archive"));
        _builderExternalSourceKindOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.SourceKindPackageSource, "Package Source"));

        _builderExternalIntakeModeOptions.Clear();
        _builderExternalIntakeModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.IntakeModeMetadataOnly, "Metadata Only"));
        _builderExternalIntakeModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.IntakeModeSnapshotForReview, "Snapshot For Review"));
        _builderExternalIntakeModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.IntakeModeReferenceOnly, "Reference Only"));
        _builderExternalIntakeModeOptions.Add(new BuilderRecoveryOptionRow(BuilderExternalReconService.IntakeModeVendorCandidate, "Vendor Candidate"));
    }

    private void NotifyBuilderExternalReconStateChanged()
    {
        OnPropertyChanged(nameof(BuilderExternalReconSummary));
        OnPropertyChanged(nameof(BuilderExternalModeSummary));
        OnPropertyChanged(nameof(BuilderExternalDisabledReason));
        OnPropertyChanged(nameof(BuilderExternalLatestMetadataSummary));
        OnPropertyChanged(nameof(BuilderExternalLatestSnapshotSummary));
        OnPropertyChanged(nameof(BuilderExternalLatestEvaluationSummary));
        OnPropertyChanged(nameof(BuilderExternalLatestVendorCandidateSummary));
        OnPropertyChanged(nameof(BuilderExternalProvenanceSummary));
        OnPropertyChanged(nameof(BuilderExternalReconArtifactPath));
        OnPropertyChanged(nameof(BuilderExternalSnapshotArtifactPath));
        OnPropertyChanged(nameof(BuilderExternalEvaluationArtifactPath));
        OnPropertyChanged(nameof(BuilderExternalVendorCandidateArtifactPath));
        OnPropertyChanged(nameof(BuilderExternalProvenanceArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExternalSuggestions));
        OnPropertyChanged(nameof(HasBuilderExternalSnapshots));
        OnPropertyChanged(nameof(HasBuilderExternalEvaluations));
        OnPropertyChanged(nameof(HasBuilderExternalVendorCandidates));
        OnPropertyChanged(nameof(HasBuilderExternalProvenanceEntries));
        OnPropertyChanged(nameof(CanBuilderExternalManualIntake));
        OnPropertyChanged(nameof(CanBuilderExternalSuggestions));
        OnPropertyChanged(nameof(HasBuilderExternalReconArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExternalSnapshotArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExternalEvaluationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExternalVendorCandidateArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExternalProvenanceArtifactPath));
        OnPropertyChanged(nameof(SelectedBuilderExternalReconMode));
        OnPropertyChanged(nameof(SelectedBuilderExternalSourceKind));
        OnPropertyChanged(nameof(SelectedBuilderExternalIntakeMode));
        OnPropertyChanged(nameof(SelectedBuilderExternalSnapshotId));
        OnPropertyChanged(nameof(BuilderExternalSourceUrl));
        OnPropertyChanged(nameof(BuilderExternalRequestedRef));
        OnPropertyChanged(nameof(BuilderExternalOperatorNote));
        OpenBuilderExternalReconArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderExternalSnapshotArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderExternalEvaluationArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderExternalVendorCandidateArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderExternalProvenanceArtifactCommand?.RaiseCanExecuteChanged();
        FetchBuilderExternalMetadataCommand?.RaiseCanExecuteChanged();
        SnapshotBuilderExternalSourceCommand?.RaiseCanExecuteChanged();
        EvaluateBuilderExternalSnapshotCommand?.RaiseCanExecuteChanged();
        StageBuilderExternalVendorCandidateCommand?.RaiseCanExecuteChanged();
        NotifyBuilderPatternLibraryStateChanged();
    }
}

public sealed record BuilderExternalSuggestionRow(BuilderExternalSourceSuggestionRecord Suggestion)
{
    public string Header => Suggestion.Title;
    public string Summary => Suggestion.Summary;
    public string UsageSummary => $"Usage: {Suggestion.SuggestedUsage.Replace('_', ' ')}. Intake: {Suggestion.SuggestedIntakeMode.Replace('_', ' ')}.";
}

public sealed record BuilderExternalSnapshotRow(BuilderExternalSourceSnapshotRecord Snapshot)
{
    public string SnapshotId => Snapshot.SnapshotId;
    public string Header => $"{Snapshot.SnapshotId} ({Snapshot.SnapshotScope.Replace('_', ' ')})";
    public string Summary => Snapshot.Summary;
    public string LicenseSummary => $"License: {Snapshot.LicenseStatus.Replace('_', ' ')} {Snapshot.License}".Trim();
}

public sealed record BuilderExternalEvaluationRow(BuilderExternalCodeEvaluationRecord Evaluation)
{
    public string Header => $"{Evaluation.RecommendedUsage.Replace('_', ' ')} ({Evaluation.EvaluationId})";
    public string Summary => Evaluation.Summary;
    public string ScoreSummary => $"Usefulness {Evaluation.UsefulnessScore:0.##}. Quality {Evaluation.QualityScore:0.##}. Risk {Evaluation.RiskScore:0.##}.";
}

public sealed record BuilderExternalVendorCandidateRow(BuilderVendorCandidateRecord Candidate)
{
    public string Header => $"{Candidate.CandidateId} -> {Candidate.VendorDestinationSuggestion}";
    public string Summary => Candidate.Summary;
    public string RiskSummary => Candidate.RiskSummary;
}

public sealed record BuilderExternalProvenanceRow(BuilderExternalProvenanceEntryRecord Entry)
{
    public string Header => $"{Entry.CanonicalSourceId} ({Entry.ResolvedCommitOrContentHash})";
    public string Summary => Entry.Summary;
    public string LicenseSummary => $"License: {Entry.LicenseStatus.Replace('_', ' ')} {Entry.LicenseMetadata}".Trim();
}
