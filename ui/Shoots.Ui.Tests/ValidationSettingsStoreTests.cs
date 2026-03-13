using System;
using System.IO;
using Shoots.UI.Settings;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ValidationSettingsStoreTests
{
    [Fact]
    public void Validation_settings_store_round_trips_and_normalizes_values()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-validation-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var store = new ValidationSettingsStore(root);
            store.Save(new ValidationSettings(true, true, 50, true, true, true, 150, 50, true, 40, true, false, true, 25, 600, false, true, false, false, false, 12, false, 25, true));

            var loaded = store.Load();
            Assert.True(loaded.ContinueOnFailure);
            Assert.True(loaded.IncludeValidateBuild);
            Assert.True(loaded.AutoOpenLogsOnFailure);
            Assert.True(loaded.ValidateGeneratedOutputAfterRun);
            Assert.True(loaded.EnableStabilityRetry);
            Assert.Equal(20, loaded.KeepLastRuns);
            Assert.Equal(100, loaded.HistoryRetentionCount);
            Assert.Equal(20, loaded.RegressionComparisonWindow);
            Assert.True(loaded.CountRetryPassesAsStableInSummaries);
            Assert.Equal(20, loaded.BaselineHistoryRetentionCount);
            Assert.True(loaded.CountPassedOnRetryAsReleaseReady);
            Assert.False(loaded.FlakySuspectedBlocksReleaseReadiness);
            Assert.True(loaded.EnableSemanticReuseSuggestions);
            Assert.Equal(10, loaded.MaxSemanticReuseCases);
            Assert.Equal(500, loaded.SemanticReuseRetentionCount);
            Assert.False(loaded.IndexProviderDiagnosticsEpisodes);
            Assert.True(loaded.OnlyShowPassingOrImprovedReuseCases);
            Assert.False(loaded.IncludePromotedRepairSuggestions);
            Assert.False(loaded.IncludeProviderEpisodeSuggestions);
            Assert.False(loaded.EnablePlaybookSuggestions);
            Assert.Equal(10, loaded.MinimumPlaybookEvidenceCount);
            Assert.False(loaded.ShowTentativePlaybooks);
            Assert.Equal(10, loaded.MaxPlaybooksPerContext);
            Assert.True(loaded.EnableIsolatedValidationWorkspaceMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Validation_settings_store_returns_defaults_when_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-validation-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var store = new ValidationSettingsStore(root);
            var loaded = store.Load();

            Assert.False(loaded.ContinueOnFailure);
            Assert.False(loaded.IncludeValidateBuild);
            Assert.False(loaded.AutoOpenLogsOnFailure);
            Assert.False(loaded.ValidateGeneratedOutputAfterRun);
            Assert.False(loaded.EnableStabilityRetry);
            Assert.Equal(5, loaded.KeepLastRuns);
            Assert.Equal(20, loaded.HistoryRetentionCount);
            Assert.Equal(5, loaded.RegressionComparisonWindow);
            Assert.False(loaded.CountRetryPassesAsStableInSummaries);
            Assert.Equal(5, loaded.BaselineHistoryRetentionCount);
            Assert.False(loaded.CountPassedOnRetryAsReleaseReady);
            Assert.True(loaded.FlakySuspectedBlocksReleaseReadiness);
            Assert.False(loaded.EnableSemanticReuseSuggestions);
            Assert.Equal(5, loaded.MaxSemanticReuseCases);
            Assert.Equal(200, loaded.SemanticReuseRetentionCount);
            Assert.True(loaded.IndexProviderDiagnosticsEpisodes);
            Assert.False(loaded.OnlyShowPassingOrImprovedReuseCases);
            Assert.True(loaded.IncludePromotedRepairSuggestions);
            Assert.True(loaded.IncludeProviderEpisodeSuggestions);
            Assert.True(loaded.EnablePlaybookSuggestions);
            Assert.Equal(2, loaded.MinimumPlaybookEvidenceCount);
            Assert.True(loaded.ShowTentativePlaybooks);
            Assert.Equal(3, loaded.MaxPlaybooksPerContext);
            Assert.False(loaded.EnableIsolatedValidationWorkspaceMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
