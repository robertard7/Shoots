using System;
using System.IO;
using System.Text.Json;

namespace Shoots.UI.Settings;

public interface IValidationSettingsStore
{
    ValidationSettings Load();

    void Save(ValidationSettings settings);
}

public sealed record ValidationSettings(
    bool ContinueOnFailure,
    bool IncludeValidateBuild,
    int KeepLastRuns,
    bool AutoOpenLogsOnFailure,
    bool ValidateGeneratedOutputAfterRun,
    bool EnableStabilityRetry = false,
    int HistoryRetentionCount = 20,
    int RegressionComparisonWindow = 5,
    bool CountRetryPassesAsStableInSummaries = false,
    int BaselineHistoryRetentionCount = 5,
    bool CountPassedOnRetryAsReleaseReady = false,
    bool FlakySuspectedBlocksReleaseReadiness = true,
    bool EnableSemanticReuseSuggestions = false,
    int MaxSemanticReuseCases = 5,
    int SemanticReuseRetentionCount = 200,
    bool IndexProviderDiagnosticsEpisodes = true,
    bool OnlyShowPassingOrImprovedReuseCases = false,
    bool IncludePromotedRepairSuggestions = true,
    bool IncludeProviderEpisodeSuggestions = true,
    bool EnablePlaybookSuggestions = true,
    int MinimumPlaybookEvidenceCount = 2,
    bool ShowTentativePlaybooks = true,
    int MaxPlaybooksPerContext = 3,
    bool EnableIsolatedValidationWorkspaceMode = false)
{
    public ValidationSettings Normalize()
    {
        var keepLastRuns = Math.Clamp(KeepLastRuns, 1, 20);
        var historyRetentionCount = Math.Clamp(HistoryRetentionCount, 5, 100);
        var regressionComparisonWindow = Math.Clamp(RegressionComparisonWindow, 2, Math.Min(20, historyRetentionCount));
        var baselineHistoryRetentionCount = Math.Clamp(BaselineHistoryRetentionCount, 1, 20);
        var maxSemanticReuseCases = Math.Clamp(MaxSemanticReuseCases, 1, 10);
        var semanticReuseRetentionCount = Math.Clamp(SemanticReuseRetentionCount, 20, 500);
        var minimumPlaybookEvidenceCount = Math.Clamp(MinimumPlaybookEvidenceCount, 2, 10);
        var maxPlaybooksPerContext = Math.Clamp(MaxPlaybooksPerContext, 1, 10);
        return this with
        {
            KeepLastRuns = keepLastRuns,
            HistoryRetentionCount = historyRetentionCount,
            RegressionComparisonWindow = regressionComparisonWindow,
            BaselineHistoryRetentionCount = baselineHistoryRetentionCount,
            MaxSemanticReuseCases = maxSemanticReuseCases,
            SemanticReuseRetentionCount = semanticReuseRetentionCount,
            MinimumPlaybookEvidenceCount = minimumPlaybookEvidenceCount,
            MaxPlaybooksPerContext = maxPlaybooksPerContext
        };
    }
}

public sealed class ValidationSettingsStore : IValidationSettingsStore
{
    public const string FileName = "validation-settings.json";
    private readonly string _settingsPath;

    public ValidationSettingsStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Shoots");

        _settingsPath = Path.Combine(root, FileName);
    }

    public ValidationSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return CreateDefault();

            var settings = JsonSerializer.Deserialize<ValidationSettings>(File.ReadAllText(_settingsPath), JsonOptions());
            return (settings ?? CreateDefault()).Normalize();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(ValidationSettings settings)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(normalized, JsonOptions()));
    }

    private static ValidationSettings CreateDefault()
        => new(
            ContinueOnFailure: false,
            IncludeValidateBuild: false,
            KeepLastRuns: 5,
            AutoOpenLogsOnFailure: false,
            ValidateGeneratedOutputAfterRun: false,
            EnableStabilityRetry: false,
            HistoryRetentionCount: 20,
            RegressionComparisonWindow: 5,
            CountRetryPassesAsStableInSummaries: false,
            BaselineHistoryRetentionCount: 5,
            CountPassedOnRetryAsReleaseReady: false,
            FlakySuspectedBlocksReleaseReadiness: true,
            EnableSemanticReuseSuggestions: false,
            MaxSemanticReuseCases: 5,
            SemanticReuseRetentionCount: 200,
            IndexProviderDiagnosticsEpisodes: true,
            OnlyShowPassingOrImprovedReuseCases: false,
            IncludePromotedRepairSuggestions: true,
            IncludeProviderEpisodeSuggestions: true,
            EnablePlaybookSuggestions: true,
            MinimumPlaybookEvidenceCount: 2,
            ShowTentativePlaybooks: true,
            MaxPlaybooksPerContext: 3,
            EnableIsolatedValidationWorkspaceMode: false);

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
