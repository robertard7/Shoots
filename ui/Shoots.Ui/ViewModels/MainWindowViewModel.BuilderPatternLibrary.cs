using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderPatternLibraryEntryRow> _builderPatternLibraryEntries = new();
    private readonly ObservableCollection<BuilderPatternLibraryMatchRow> _builderPatternLibraryMatches = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderPatternLibraryTypeOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderPatternLibraryLanguageOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderPatternLibraryUsageOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderPatternLibraryLicenseOptions = new();
    private readonly ObservableCollection<string> _builderPatternLibrarySelectedKeyPaths = new();
    private readonly ObservableCollection<string> _builderPatternLibrarySelectedMatchReasons = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderPatternLibrarySelectedArtifactLinks = new();
    private BuilderPatternLibraryEntryRow[] _builderPatternLibraryAllEntries = Array.Empty<BuilderPatternLibraryEntryRow>();
    private BuilderPatternLibraryMatchRow[] _builderPatternLibraryAllMatches = Array.Empty<BuilderPatternLibraryMatchRow>();
    private BuilderPatternLibraryEntryRow? _selectedBuilderPatternLibraryEntry;
    private BuilderPatternLibraryMatchRow? _selectedBuilderPatternLibraryMatch;
    private string _builderPatternLibrarySummary = "No approved pattern library entries recorded.";
    private string _builderPatternLibraryMatchesSummary = "No approved pattern matches recorded.";
    private string _builderPatternLibraryAttachmentSummary = "No approved pattern reference attached.";
    private string _builderPatternLibraryIndexArtifactPath = string.Empty;
    private string _builderPatternLibraryEntriesArtifactPath = string.Empty;
    private string _builderPatternLibraryMatchesArtifactPath = string.Empty;
    private string _builderPatternLibraryProvenanceArtifactPath = string.Empty;
    private string _builderPatternLibrarySearchText = string.Empty;
    private string _selectedBuilderPatternLibraryType = "all";
    private string _selectedBuilderPatternLibraryLanguage = "all";
    private string _selectedBuilderPatternLibraryUsageClass = "all";
    private string _selectedBuilderPatternLibraryLicenseState = "all";

    public ReadOnlyObservableCollection<BuilderPatternLibraryEntryRow> BuilderPatternLibraryEntries { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderPatternLibraryMatchRow> BuilderPatternLibraryMatches { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderPatternLibraryTypeOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderPatternLibraryLanguageOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderPatternLibraryUsageOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderPatternLibraryLicenseOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternLibrarySelectedKeyPaths { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderPatternLibrarySelectedMatchReasons { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderPatternLibrarySelectedArtifactLinks { get; private set; } = null!;

    public bool HasBuilderPatternLibraryEntries => _builderPatternLibraryEntries.Count > 0;
    public bool HasBuilderPatternLibraryMatches => _builderPatternLibraryMatches.Count > 0;
    public string BuilderPatternLibrarySummary => _builderPatternLibrarySummary;
    public string BuilderPatternLibraryMatchesSummary => _builderPatternLibraryMatchesSummary;
    public string BuilderPatternLibraryAttachmentSummary => _builderPatternLibraryAttachmentSummary;
    public string BuilderPatternLibraryIndexArtifactPath => _builderPatternLibraryIndexArtifactPath;
    public string BuilderPatternLibraryEntriesArtifactPath => _builderPatternLibraryEntriesArtifactPath;
    public string BuilderPatternLibraryMatchesArtifactPath => _builderPatternLibraryMatchesArtifactPath;
    public string BuilderPatternLibraryProvenanceArtifactPath => _builderPatternLibraryProvenanceArtifactPath;
    public bool HasBuilderPatternLibraryIndexArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternLibraryIndexArtifactPath) && File.Exists(_builderPatternLibraryIndexArtifactPath);
    public bool HasBuilderPatternLibraryEntriesArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternLibraryEntriesArtifactPath) && File.Exists(_builderPatternLibraryEntriesArtifactPath);
    public bool HasBuilderPatternLibraryMatchesArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternLibraryMatchesArtifactPath) && File.Exists(_builderPatternLibraryMatchesArtifactPath);
    public bool HasBuilderPatternLibraryProvenanceArtifactPath => !string.IsNullOrWhiteSpace(_builderPatternLibraryProvenanceArtifactPath) && File.Exists(_builderPatternLibraryProvenanceArtifactPath);
    public bool HasSelectedBuilderPatternLibraryEntry => _selectedBuilderPatternLibraryEntry is not null;
    public string BuilderPatternLibrarySelectedTitle => _selectedBuilderPatternLibraryEntry?.Header ?? "No approved pattern selected.";
    public string BuilderPatternLibrarySelectedSummary => _selectedBuilderPatternLibraryEntry?.Summary ?? "No approved pattern selected.";
    public string BuilderPatternLibrarySelectedUsageSummary => _selectedBuilderPatternLibraryEntry?.UsageSummary ?? string.Empty;
    public string BuilderPatternLibrarySelectedLicenseSummary => _selectedBuilderPatternLibraryEntry?.LicenseSummary ?? string.Empty;
    public string BuilderPatternLibrarySelectedProvenanceSummary => _selectedBuilderPatternLibraryEntry?.ProvenanceSummary ?? string.Empty;
    public string BuilderPatternLibrarySelectedMatchSummary => _selectedBuilderPatternLibraryEntry?.MatchSummary ?? string.Empty;
    public bool HasBuilderPatternLibrarySelectedKeyPaths => _builderPatternLibrarySelectedKeyPaths.Count > 0;
    public bool HasBuilderPatternLibrarySelectedMatchReasons => _builderPatternLibrarySelectedMatchReasons.Count > 0;
    public bool HasBuilderPatternLibrarySelectedArtifactLinks => _builderPatternLibrarySelectedArtifactLinks.Count > 0;
    public bool CanApproveBuilderPatternSnapshot => !string.IsNullOrWhiteSpace(_selectedBuilderExternalSnapshotId) && HasBuilderExternalSnapshots;
    public bool CanApproveBuilderPatternVendorCandidate => HasBuilderExternalVendorCandidates &&
                                                           _builderExternalVendorCandidates.Any(row => string.Equals(row.Candidate.SnapshotId, _selectedBuilderExternalSnapshotId, StringComparison.OrdinalIgnoreCase));
    public bool CanAttachBuilderPatternReference => _selectedBuilderPatternLibraryEntry is not null;
    public string BuilderPatternLibraryFilterSummary => _builderPatternLibraryAllEntries.Length == 0
        ? "No approved pattern entries loaded."
        : $"Showing {_builderPatternLibraryEntries.Count} of {_builderPatternLibraryAllEntries.Length} approved pattern entr{(_builderPatternLibraryAllEntries.Length == 1 ? "y" : "ies")} with {_builderPatternLibraryMatches.Count} workspace match(es).";
    public string BuilderRecoveryPatternLibrarySummary => BuildBuilderPatternOverlaySummary("Recovery Guidance");
    public string BuilderRecoverySimulationPatternLibrarySummary => BuildBuilderPatternOverlaySummary("What-If Analysis");
    public string BuilderRecoveryComparisonPatternLibrarySummary => BuildBuilderPatternOverlaySummary("Compare Recovery Options");
    public string BuilderExternalPatternLibrarySummary => BuildBuilderPatternOverlaySummary("External Code Intake");

    public string BuilderPatternLibrarySearchText
    {
        get => _builderPatternLibrarySearchText;
        set
        {
            if (string.Equals(_builderPatternLibrarySearchText, value, StringComparison.Ordinal))
            {
                return;
            }

            _builderPatternLibrarySearchText = value ?? string.Empty;
            OnPropertyChanged(nameof(BuilderPatternLibrarySearchText));
            ApplyBuilderPatternLibraryFilters();
        }
    }

    public string SelectedBuilderPatternLibraryType
    {
        get => _selectedBuilderPatternLibraryType;
        set
        {
            if (string.Equals(_selectedBuilderPatternLibraryType, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderPatternLibraryType = value ?? "all";
            OnPropertyChanged(nameof(SelectedBuilderPatternLibraryType));
            ApplyBuilderPatternLibraryFilters();
        }
    }

    public string SelectedBuilderPatternLibraryLanguage
    {
        get => _selectedBuilderPatternLibraryLanguage;
        set
        {
            if (string.Equals(_selectedBuilderPatternLibraryLanguage, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderPatternLibraryLanguage = value ?? "all";
            OnPropertyChanged(nameof(SelectedBuilderPatternLibraryLanguage));
            ApplyBuilderPatternLibraryFilters();
        }
    }

    public string SelectedBuilderPatternLibraryUsageClass
    {
        get => _selectedBuilderPatternLibraryUsageClass;
        set
        {
            if (string.Equals(_selectedBuilderPatternLibraryUsageClass, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderPatternLibraryUsageClass = value ?? "all";
            OnPropertyChanged(nameof(SelectedBuilderPatternLibraryUsageClass));
            ApplyBuilderPatternLibraryFilters();
        }
    }

    public string SelectedBuilderPatternLibraryLicenseState
    {
        get => _selectedBuilderPatternLibraryLicenseState;
        set
        {
            if (string.Equals(_selectedBuilderPatternLibraryLicenseState, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderPatternLibraryLicenseState = value ?? "all";
            OnPropertyChanged(nameof(SelectedBuilderPatternLibraryLicenseState));
            ApplyBuilderPatternLibraryFilters();
        }
    }

    public AsyncRelayCommand OpenBuilderPatternLibraryIndexArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternLibraryEntriesArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternLibraryMatchesArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderPatternLibraryProvenanceArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand ApproveBuilderPatternSnapshotCommand { get; private set; } = null!;
    public AsyncRelayCommand ApproveBuilderPatternVendorCandidateCommand { get; private set; } = null!;
    public AsyncRelayCommand AttachBuilderPatternReferenceCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternLibraryEntryRow> SelectBuilderPatternLibraryEntryCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternLibraryMatchRow> SelectBuilderPatternLibraryMatchCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderPatternLibraryArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderPatternLibrarySurface()
    {
        BuilderPatternLibraryEntries = new ReadOnlyObservableCollection<BuilderPatternLibraryEntryRow>(_builderPatternLibraryEntries);
        BuilderPatternLibraryMatches = new ReadOnlyObservableCollection<BuilderPatternLibraryMatchRow>(_builderPatternLibraryMatches);
        BuilderPatternLibraryTypeOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderPatternLibraryTypeOptions);
        BuilderPatternLibraryLanguageOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderPatternLibraryLanguageOptions);
        BuilderPatternLibraryUsageOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderPatternLibraryUsageOptions);
        BuilderPatternLibraryLicenseOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderPatternLibraryLicenseOptions);
        BuilderPatternLibrarySelectedKeyPaths = new ReadOnlyObservableCollection<string>(_builderPatternLibrarySelectedKeyPaths);
        BuilderPatternLibrarySelectedMatchReasons = new ReadOnlyObservableCollection<string>(_builderPatternLibrarySelectedMatchReasons);
        BuilderPatternLibrarySelectedArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderPatternLibrarySelectedArtifactLinks);
        PopulateBuilderPatternLibraryFilterOptions(Array.Empty<BuilderPatternLibraryEntryRow>());
        OpenBuilderPatternLibraryIndexArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternLibraryIndexArtifactPath), () => HasBuilderPatternLibraryIndexArtifactPath);
        OpenBuilderPatternLibraryEntriesArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternLibraryEntriesArtifactPath), () => HasBuilderPatternLibraryEntriesArtifactPath);
        OpenBuilderPatternLibraryMatchesArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternLibraryMatchesArtifactPath), () => HasBuilderPatternLibraryMatchesArtifactPath);
        OpenBuilderPatternLibraryProvenanceArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPatternLibraryProvenanceArtifactPath), () => HasBuilderPatternLibraryProvenanceArtifactPath);
        ApproveBuilderPatternSnapshotCommand = new AsyncRelayCommand(ApproveBuilderPatternSnapshotAsync, () => CanApproveBuilderPatternSnapshot);
        ApproveBuilderPatternVendorCandidateCommand = new AsyncRelayCommand(ApproveBuilderPatternVendorCandidateAsync, () => CanApproveBuilderPatternVendorCandidate);
        AttachBuilderPatternReferenceCommand = new AsyncRelayCommand(AttachBuilderPatternReferenceAsync, () => CanAttachBuilderPatternReference);
        SelectBuilderPatternLibraryEntryCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternLibraryEntryRow>(SelectBuilderPatternLibraryEntryAsync, row => row is not null);
        SelectBuilderPatternLibraryMatchCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPatternLibraryMatchRow>(SelectBuilderPatternLibraryMatchAsync, row => row is not null);
        OpenBuilderPatternLibraryArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderPatternLibraryArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderPatternLibraryArtifacts()
    {
        var repoRoot = GetBuilderWorkspaceRepoRoot();
        var entriesArtifact = BuilderPatternLibraryService.LoadPatternLibraryEntries(repoRoot);
        var indexArtifact = BuilderPatternLibraryService.LoadPatternLibraryIndex(repoRoot);
        var provenanceArtifact = BuilderPatternLibraryService.LoadPatternLibraryProvenance(repoRoot);
        var matchesArtifact = entriesArtifact?.Entries.Count > 0
            ? BuilderPatternLibraryService.RefreshPatternLibraryMatches(repoRoot, entriesArtifact)
            : BuilderPatternLibraryService.LoadPatternLibraryMatches(repoRoot);

        if (entriesArtifact is null && indexArtifact is null && provenanceArtifact is null && matchesArtifact is null)
        {
            ResetBuilderPatternLibraryState();
            return;
        }

        _builderPatternLibrarySummary = entriesArtifact?.Summary ?? "No approved pattern library entries recorded.";
        _builderPatternLibraryMatchesSummary = matchesArtifact?.Summary ?? "No approved pattern matches recorded.";
        _builderPatternLibraryAttachmentSummary = BuildBuilderPatternAttachmentSummary(entriesArtifact, matchesArtifact);
        _builderPatternLibraryIndexArtifactPath = indexArtifact?.ArtifactPath ?? string.Empty;
        _builderPatternLibraryEntriesArtifactPath = entriesArtifact?.ArtifactPath ?? string.Empty;
        _builderPatternLibraryMatchesArtifactPath = matchesArtifact?.ArtifactPath ?? string.Empty;
        _builderPatternLibraryProvenanceArtifactPath = provenanceArtifact?.ArtifactPath ?? string.Empty;

        var provenanceIndex = (provenanceArtifact?.Entries ?? Array.Empty<BuilderPatternLibraryProvenanceEntryRecord>())
            .GroupBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.ObservedUtc).ThenBy(entry => entry.ProvenanceId, StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
        var matchIndex = (matchesArtifact?.Matches ?? Array.Empty<BuilderPatternLibraryMatchRecord>())
            .GroupBy(entry => entry.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.MatchScore).ThenBy(entry => entry.MatchId, StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);

        _builderPatternLibraryAllEntries = (entriesArtifact?.Entries ?? Array.Empty<BuilderPatternLibraryEntryRecord>())
            .Select(entry => new BuilderPatternLibraryEntryRow(
                entry,
                provenanceIndex.TryGetValue(entry.PatternEntryId, out var provenance) ? provenance : null,
                matchIndex.TryGetValue(entry.PatternEntryId, out var match) ? match : null,
                matchesArtifact is not null && string.Equals(matchesArtifact.AttachedPatternEntryId, entry.PatternEntryId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(row => row.MatchScore)
            .ThenBy(row => row.PatternTypeLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PatternName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PatternEntryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _builderPatternLibraryAllMatches = (matchesArtifact?.Matches ?? Array.Empty<BuilderPatternLibraryMatchRecord>())
            .Select(match =>
            {
                var entry = _builderPatternLibraryAllEntries.FirstOrDefault(row => string.Equals(row.PatternEntryId, match.PatternEntryId, StringComparison.OrdinalIgnoreCase));
                return new BuilderPatternLibraryMatchRow(match, entry);
            })
            .OrderByDescending(row => row.MatchScore)
            .ThenBy(row => row.PatternName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.MatchId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PopulateBuilderPatternLibraryFilterOptions(_builderPatternLibraryAllEntries);
        ApplyBuilderPatternLibraryFilters();
    }

    private void ResetBuilderPatternLibraryState()
    {
        _builderPatternLibrarySummary = "No approved pattern library entries recorded.";
        _builderPatternLibraryMatchesSummary = "No approved pattern matches recorded.";
        _builderPatternLibraryAttachmentSummary = "No approved pattern reference attached.";
        _builderPatternLibraryIndexArtifactPath = string.Empty;
        _builderPatternLibraryEntriesArtifactPath = string.Empty;
        _builderPatternLibraryMatchesArtifactPath = string.Empty;
        _builderPatternLibraryProvenanceArtifactPath = string.Empty;
        _builderPatternLibraryAllEntries = Array.Empty<BuilderPatternLibraryEntryRow>();
        _builderPatternLibraryAllMatches = Array.Empty<BuilderPatternLibraryMatchRow>();
        _builderPatternLibraryEntries.Clear();
        _builderPatternLibraryMatches.Clear();
        _selectedBuilderPatternLibraryEntry = null;
        _selectedBuilderPatternLibraryMatch = null;
        _builderPatternLibrarySelectedKeyPaths.Clear();
        _builderPatternLibrarySelectedMatchReasons.Clear();
        _builderPatternLibrarySelectedArtifactLinks.Clear();
        PopulateBuilderPatternLibraryFilterOptions(Array.Empty<BuilderPatternLibraryEntryRow>());
        NotifyBuilderPatternLibraryStateChanged();
    }

    private void ApplyBuilderPatternLibraryFilters()
    {
        var filteredEntries = _builderPatternLibraryAllEntries
            .Where(MatchesBuilderPatternLibraryFilters)
            .ToArray();
        var filteredMatches = _builderPatternLibraryAllMatches
            .Where(row => row.Entry is null || filteredEntries.Any(entry => string.Equals(entry.PatternEntryId, row.PatternEntryId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        _builderPatternLibraryEntries.Clear();
        foreach (var row in filteredEntries)
        {
            _builderPatternLibraryEntries.Add(row);
        }

        _builderPatternLibraryMatches.Clear();
        foreach (var row in filteredMatches)
        {
            _builderPatternLibraryMatches.Add(row);
        }

        var selectedEntryId = _selectedBuilderPatternLibraryEntry?.PatternEntryId;
        var nextSelectedEntry = filteredEntries.FirstOrDefault(row => string.Equals(row.PatternEntryId, selectedEntryId, StringComparison.OrdinalIgnoreCase))
                                ?? filteredEntries.FirstOrDefault();
        ApplySelectedBuilderPatternLibraryEntry(nextSelectedEntry);

        var selectedMatchId = _selectedBuilderPatternLibraryMatch?.MatchId;
        _selectedBuilderPatternLibraryMatch = filteredMatches.FirstOrDefault(row => string.Equals(row.MatchId, selectedMatchId, StringComparison.OrdinalIgnoreCase))
                                           ?? filteredMatches.FirstOrDefault(row => string.Equals(row.PatternEntryId, nextSelectedEntry?.PatternEntryId, StringComparison.OrdinalIgnoreCase))
                                           ?? filteredMatches.FirstOrDefault();
        NotifyBuilderPatternLibraryStateChanged();
    }

    private bool MatchesBuilderPatternLibraryFilters(BuilderPatternLibraryEntryRow row)
    {
        if (!string.Equals(_selectedBuilderPatternLibraryType, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Entry.PatternType, _selectedBuilderPatternLibraryType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderPatternLibraryLanguage, "all", StringComparison.OrdinalIgnoreCase) &&
            !row.Entry.LanguageSet.Contains(_selectedBuilderPatternLibraryLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderPatternLibraryUsageClass, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Entry.ApprovedUsageClass, _selectedBuilderPatternLibraryUsageClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderPatternLibraryLicenseState, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Entry.LicenseStatus, _selectedBuilderPatternLibraryLicenseState, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_builderPatternLibrarySearchText))
        {
            return true;
        }

        var search = _builderPatternLibrarySearchText.Trim();
        return row.PatternName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.PatternTypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Entry.KeyPaths.Any(path => path.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               row.LanguageSummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.UsageSummary.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateBuilderPatternLibraryFilterOptions(IReadOnlyList<BuilderPatternLibraryEntryRow> rows)
    {
        PopulateBuilderPatternLibraryOptionCollection(_builderPatternLibraryTypeOptions, "All pattern types", rows.Select(row => new BuilderRecoveryOptionRow(row.Entry.PatternType, row.PatternTypeLabel)));
        PopulateBuilderPatternLibraryOptionCollection(_builderPatternLibraryLanguageOptions, "All languages", rows.SelectMany(row => row.Entry.LanguageSet.Select(language => new BuilderRecoveryOptionRow(language, language))));
        PopulateBuilderPatternLibraryOptionCollection(_builderPatternLibraryUsageOptions, "All usage classes", rows.Select(row => new BuilderRecoveryOptionRow(row.Entry.ApprovedUsageClass, BuilderPatternLibraryService.GetUsageClassLabel(row.Entry.ApprovedUsageClass))));
        PopulateBuilderPatternLibraryOptionCollection(_builderPatternLibraryLicenseOptions, "All license states", rows.Select(row => new BuilderRecoveryOptionRow(row.Entry.LicenseStatus, row.Entry.LicenseStatus.Replace('_', ' '))));
        _selectedBuilderPatternLibraryType = EnsureBuilderPatternLibraryFilterValue(_selectedBuilderPatternLibraryType, _builderPatternLibraryTypeOptions);
        _selectedBuilderPatternLibraryLanguage = EnsureBuilderPatternLibraryFilterValue(_selectedBuilderPatternLibraryLanguage, _builderPatternLibraryLanguageOptions);
        _selectedBuilderPatternLibraryUsageClass = EnsureBuilderPatternLibraryFilterValue(_selectedBuilderPatternLibraryUsageClass, _builderPatternLibraryUsageOptions);
        _selectedBuilderPatternLibraryLicenseState = EnsureBuilderPatternLibraryFilterValue(_selectedBuilderPatternLibraryLicenseState, _builderPatternLibraryLicenseOptions);
    }

    private static void PopulateBuilderPatternLibraryOptionCollection(
        ObservableCollection<BuilderRecoveryOptionRow> collection,
        string allLabel,
        IEnumerable<BuilderRecoveryOptionRow> options)
    {
        collection.Clear();
        collection.Add(new BuilderRecoveryOptionRow("all", allLabel));
        foreach (var option in options
                     .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                     .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase))
        {
            collection.Add(option);
        }
    }

    private static string EnsureBuilderPatternLibraryFilterValue(string value, ObservableCollection<BuilderRecoveryOptionRow> options)
        => options.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) ? value : "all";

    private void ApplySelectedBuilderPatternLibraryEntry(BuilderPatternLibraryEntryRow? row)
    {
        _selectedBuilderPatternLibraryEntry = row;
        _builderPatternLibrarySelectedKeyPaths.Clear();
        _builderPatternLibrarySelectedMatchReasons.Clear();
        _builderPatternLibrarySelectedArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var path in row.Entry.KeyPaths)
            {
                _builderPatternLibrarySelectedKeyPaths.Add(path);
            }

            foreach (var reason in row.MatchReasons)
            {
                _builderPatternLibrarySelectedMatchReasons.Add(reason);
            }

            foreach (var artifact in row.ArtifactLinks)
            {
                _builderPatternLibrarySelectedArtifactLinks.Add(artifact);
            }
        }
    }

    private async Task ApproveBuilderPatternSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedBuilderExternalSnapshotId))
        {
            return;
        }

        await Task.Run(() => BuilderPatternLibraryService.ApproveSnapshotAsPatternEntry(GetBuilderWorkspaceRepoRoot(), _selectedBuilderExternalSnapshotId)).ConfigureAwait(true);
        LoadBuilderPatternLibraryArtifacts();
    }

    private async Task ApproveBuilderPatternVendorCandidateAsync()
    {
        var candidateId = _builderExternalVendorCandidates
            .FirstOrDefault(row => string.Equals(row.Candidate.SnapshotId, _selectedBuilderExternalSnapshotId, StringComparison.OrdinalIgnoreCase))
            ?.Candidate.CandidateId;
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            return;
        }

        await Task.Run(() => BuilderPatternLibraryService.ApproveVendorCandidateAsPatternEntry(GetBuilderWorkspaceRepoRoot(), candidateId)).ConfigureAwait(true);
        LoadBuilderPatternLibraryArtifacts();
    }

    private async Task AttachBuilderPatternReferenceAsync()
    {
        var entry = _selectedBuilderPatternLibraryEntry;
        if (entry is null)
        {
            return;
        }

        var matchId = _selectedBuilderPatternLibraryMatch is not null &&
                      string.Equals(_selectedBuilderPatternLibraryMatch.PatternEntryId, entry.PatternEntryId, StringComparison.OrdinalIgnoreCase)
            ? _selectedBuilderPatternLibraryMatch.MatchId
            : string.Empty;
        await Task.Run(() => BuilderPatternLibraryService.AttachPatternReference(GetBuilderWorkspaceRepoRoot(), entry.PatternEntryId, matchId)).ConfigureAwait(true);
        LoadBuilderPatternLibraryArtifacts();
    }

    private Task SelectBuilderPatternLibraryEntryAsync(BuilderPatternLibraryEntryRow? row)
    {
        ApplySelectedBuilderPatternLibraryEntry(row);
        _selectedBuilderPatternLibraryMatch = row is null
            ? null
            : _builderPatternLibraryMatches.FirstOrDefault(match => string.Equals(match.PatternEntryId, row.PatternEntryId, StringComparison.OrdinalIgnoreCase));
        NotifyBuilderPatternLibraryStateChanged();
        return Task.CompletedTask;
    }

    private Task SelectBuilderPatternLibraryMatchAsync(BuilderPatternLibraryMatchRow? row)
    {
        _selectedBuilderPatternLibraryMatch = row;
        if (row is not null)
        {
            ApplySelectedBuilderPatternLibraryEntry(_builderPatternLibraryEntries.FirstOrDefault(entry => string.Equals(entry.PatternEntryId, row.PatternEntryId, StringComparison.OrdinalIgnoreCase))
                                                   ?? _builderPatternLibraryAllEntries.FirstOrDefault(entry => string.Equals(entry.PatternEntryId, row.PatternEntryId, StringComparison.OrdinalIgnoreCase)));
        }

        NotifyBuilderPatternLibraryStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderPatternLibraryArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private string BuildBuilderPatternAttachmentSummary(
        BuilderPatternLibraryEntriesRecord? entries,
        BuilderPatternLibraryMatchesRecord? matches)
    {
        if (entries is null || matches is null || string.IsNullOrWhiteSpace(matches.AttachedPatternEntryId))
        {
            return "No approved pattern reference attached.";
        }

        var entry = entries.Entries.FirstOrDefault(item => string.Equals(item.PatternEntryId, matches.AttachedPatternEntryId, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? $"Attached pattern reference: {matches.AttachedPatternEntryId}."
            : $"Attached pattern reference: {entry.PatternName} ({BuilderPatternLibraryService.GetUsageClassLabel(entry.ApprovedUsageClass)}).";
    }

    private string BuildBuilderPatternOverlaySummary(string surfaceName)
    {
        if (_builderPatternLibraryAllMatches.Length == 0)
        {
            return $"{surfaceName}: no approved pattern matches recorded.";
        }

        var topMatch = _builderPatternLibraryAllMatches[0];
        return $"{surfaceName}: {topMatch.PatternName} is the current top approved pattern match at {topMatch.MatchScore:0.##}. {_builderPatternLibraryAttachmentSummary}";
    }

    private void NotifyBuilderPatternLibraryStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderPatternLibraryEntries));
        OnPropertyChanged(nameof(HasBuilderPatternLibraryMatches));
        OnPropertyChanged(nameof(BuilderPatternLibrarySummary));
        OnPropertyChanged(nameof(BuilderPatternLibraryMatchesSummary));
        OnPropertyChanged(nameof(BuilderPatternLibraryAttachmentSummary));
        OnPropertyChanged(nameof(BuilderPatternLibraryIndexArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternLibraryEntriesArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternLibraryMatchesArtifactPath));
        OnPropertyChanged(nameof(BuilderPatternLibraryProvenanceArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternLibraryIndexArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternLibraryEntriesArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternLibraryMatchesArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPatternLibraryProvenanceArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderPatternLibraryEntry));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedTitle));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedSummary));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedUsageSummary));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedLicenseSummary));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedProvenanceSummary));
        OnPropertyChanged(nameof(BuilderPatternLibrarySelectedMatchSummary));
        OnPropertyChanged(nameof(HasBuilderPatternLibrarySelectedKeyPaths));
        OnPropertyChanged(nameof(HasBuilderPatternLibrarySelectedMatchReasons));
        OnPropertyChanged(nameof(HasBuilderPatternLibrarySelectedArtifactLinks));
        OnPropertyChanged(nameof(CanApproveBuilderPatternSnapshot));
        OnPropertyChanged(nameof(CanApproveBuilderPatternVendorCandidate));
        OnPropertyChanged(nameof(CanAttachBuilderPatternReference));
        OnPropertyChanged(nameof(BuilderPatternLibraryFilterSummary));
        OnPropertyChanged(nameof(SelectedBuilderPatternLibraryType));
        OnPropertyChanged(nameof(SelectedBuilderPatternLibraryLanguage));
        OnPropertyChanged(nameof(SelectedBuilderPatternLibraryUsageClass));
        OnPropertyChanged(nameof(SelectedBuilderPatternLibraryLicenseState));
        OnPropertyChanged(nameof(BuilderRecoveryPatternLibrarySummary));
        OnPropertyChanged(nameof(BuilderRecoverySimulationPatternLibrarySummary));
        OnPropertyChanged(nameof(BuilderRecoveryComparisonPatternLibrarySummary));
        OnPropertyChanged(nameof(BuilderExternalPatternLibrarySummary));
        OpenBuilderPatternLibraryIndexArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternLibraryEntriesArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternLibraryMatchesArtifactCommand?.RaiseCanExecuteChanged();
        OpenBuilderPatternLibraryProvenanceArtifactCommand?.RaiseCanExecuteChanged();
        ApproveBuilderPatternSnapshotCommand?.RaiseCanExecuteChanged();
        ApproveBuilderPatternVendorCandidateCommand?.RaiseCanExecuteChanged();
        AttachBuilderPatternReferenceCommand?.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderPatternLibraryEntryRow(
    BuilderPatternLibraryEntryRecord Entry,
    BuilderPatternLibraryProvenanceEntryRecord? Provenance,
    BuilderPatternLibraryMatchRecord? Match,
    bool IsAttachedReference)
{
    public string PatternEntryId => Entry.PatternEntryId;
    public string PatternName => Entry.PatternName;
    public string Header => $"{Entry.PatternName} [{PatternTypeLabel}]";
    public string Summary => Entry.PatternSummary;
    public string PatternTypeLabel => BuilderPatternLibraryService.GetPatternTypeLabel(Entry.PatternType);
    public string UsageSummary => $"Usage: {BuilderPatternLibraryService.GetUsageClassLabel(Entry.ApprovedUsageClass)}.";
    public string LicenseSummary => $"License: {Entry.LicenseStatus.Replace('_', ' ')}.";
    public string LanguageSummary => Entry.LanguageSet.Count == 0 ? "Languages: none recorded." : $"Languages: {string.Join(", ", Entry.LanguageSet)}.";
    public string ProvenanceSummary => Provenance?.Summary ?? $"Source snapshot: {Entry.SourceSnapshotId}.";
    public string MatchSummary => Match is null ? "No workspace match scored yet." : Match.Summary;
    public double MatchScore => Match?.MatchScore ?? 0d;
    public IReadOnlyList<string> MatchReasons => Match?.MatchReasons ?? Array.Empty<string>();
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Entry.ArtifactLinks.Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path)).ToArray();
}

public sealed record BuilderPatternLibraryMatchRow(BuilderPatternLibraryMatchRecord Match, BuilderPatternLibraryEntryRow? Entry)
{
    public string MatchId => Match.MatchId;
    public string PatternEntryId => Match.PatternEntryId;
    public string PatternName => Entry?.PatternName ?? Match.PatternEntryId;
    public string Header => $"{PatternName} [{Match.FitClassification.Replace('_', ' ')}]";
    public string Summary => Match.Summary;
    public double MatchScore => Match.MatchScore;
}
