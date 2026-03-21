using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderOperatorDecisionRequest(
    string PlaybookId,
    string SimulationId,
    string ActionTaken,
    string TargetRepo,
    string TargetRoute,
    IReadOnlyList<string> TriggerArtifacts,
    string ResultRunId,
    string ResultState,
    bool SuccessFlag,
    string FailureClass,
    IReadOnlyList<string> ResultArtifacts,
    string SimulationScenario = "",
    string PredictedOutcome = "",
    string PredictedOutcomeClass = "",
    string PredictedConfidenceLevel = "",
    double PredictedConfidenceScore = 0d,
    string ActiveSignalProfileId = "",
    string ProfileOverrideHash = "",
    string CalibrationSnapshotLink = "",
    string PatternEntryId = "",
    string PatternMatchId = "",
    string PatternLibrarySnapshotId = "",
    string PatchCandidateId = "",
    string PatchProvenanceId = "");

public sealed record BuilderOperatorDecisionRecord(
    string DecisionId,
    DateTimeOffset Timestamp,
    string PlaybookId,
    string SimulationId,
    string ActionTaken,
    string TargetRepo,
    string TargetRoute,
    IReadOnlyList<string> TriggerArtifacts,
    string ResultRunId,
    string ResultState,
    bool SuccessFlag,
    string FailureClass,
    IReadOnlyList<string> ResultArtifacts,
    string Summary,
    string SimulationScenario = "",
    string PredictedOutcome = "",
    string PredictedOutcomeClass = "",
    string PredictedConfidenceLevel = "",
    double PredictedConfidenceScore = 0d,
    string ActiveSignalProfileId = "",
    string ProfileOverrideHash = "",
    string CalibrationSnapshotLink = "",
    string PatternEntryId = "",
    string PatternMatchId = "",
    string PatternLibrarySnapshotId = "",
    string PatchCandidateId = "",
    string PatchProvenanceId = "")
{
    public string OutcomeSummary
        => $"Outcome: {FormatValue(ResultState)}. Success flag: {SuccessFlag}. Failure class: {FormatValue(FailureClass)}.";

    public string PredictionSummary
        => string.IsNullOrWhiteSpace(PredictedOutcomeClass)
            ? "Prediction snapshot was not recorded for this operator decision."
            : $"Predicted {FormatValue(PredictedOutcomeClass)} at {PredictedConfidenceScore:P0} confidence ({FormatValue(PredictedConfidenceLevel)}). Scenario: {FormatValue(SimulationScenario)}.";

    public string SignalProfileSummary
        => string.IsNullOrWhiteSpace(ActiveSignalProfileId)
            ? "Signal profile snapshot was not recorded for this operator decision."
            : $"Signal profile {FormatValue(ActiveSignalProfileId)} with override snapshot {FormatValue(ProfileOverrideHash)}.";

    public string PatternReferenceSummary
        => string.IsNullOrWhiteSpace(PatternEntryId)
            ? "Approved pattern reference was not recorded for this operator decision."
            : $"Approved pattern reference {FormatValue(PatternEntryId)} with match {FormatValue(PatternMatchId)} from snapshot {FormatValue(PatternLibrarySnapshotId)}.";

    public string PatchCandidateSummary
        => string.IsNullOrWhiteSpace(PatchCandidateId)
            ? "Synthesized patch candidate context was not recorded for this operator decision."
            : $"Synthesized patch candidate {FormatValue(PatchCandidateId)} with provenance {FormatValue(PatchProvenanceId)}.";

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderOperatorDecisionsRecord(
    string WorkspaceId,
    string SchemaVersion,
    IReadOnlyList<BuilderOperatorDecisionRecord> Decisions,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public static class BuilderOperatorDecisionService
{
    public const string OperatorDecisionsFileName = "builder_operator_decisions.json";

    private const string SchemaVersion = "builder_operator_decisions.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string OperatorDecisionsPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), OperatorDecisionsFileName);

    public static BuilderOperatorDecisionsRecord? LoadOperatorDecisions(string repoRoot)
        => Load<BuilderOperatorDecisionsRecord>(OperatorDecisionsPathForRepo(repoRoot));

    public static BuilderOperatorDecisionsRecord RecordDecision(
        string repoRoot,
        BuilderOperatorDecisionRequest request,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(request);

        var workspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoRoot);
        var path = OperatorDecisionsPathForRepo(repoRoot);
        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var decision = BuildDecisionRecord(request, effectiveObservedUtc);

        var existing = LoadOperatorDecisions(repoRoot);
        var decisions = existing?.Decisions.ToList() ?? new List<BuilderOperatorDecisionRecord>();
        if (!decisions.Any(entry => string.Equals(entry.DecisionId, decision.DecisionId, StringComparison.OrdinalIgnoreCase)))
        {
            decisions.Add(decision);
        }

        var artifact = new BuilderOperatorDecisionsRecord(
            workspaceId,
            SchemaVersion,
            decisions.ToArray(),
            true,
            BuildSummary(workspaceId, decisions),
            path,
            decisions.Count == 0 ? effectiveObservedUtc : decisions[^1].Timestamp);
        Save(path, artifact);
        return artifact;
    }

    private static BuilderOperatorDecisionRecord BuildDecisionRecord(
        BuilderOperatorDecisionRequest request,
        DateTimeOffset observedUtc)
    {
        var triggerArtifacts = request.TriggerArtifacts
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resultArtifacts = request.ResultArtifacts
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var decisionId = ComputeDeterministicId(
            request.PlaybookId,
            request.SimulationId,
            request.ActionTaken,
            request.TargetRepo,
            request.TargetRoute,
            request.ResultRunId,
            request.ResultState,
            request.FailureClass,
            request.ActiveSignalProfileId,
            request.ProfileOverrideHash,
            request.PatternEntryId,
            request.PatternMatchId,
            request.PatternLibrarySnapshotId,
            request.PatchCandidateId,
            request.PatchProvenanceId,
            string.Join("|", triggerArtifacts),
            string.Join("|", resultArtifacts));

        return new BuilderOperatorDecisionRecord(
            decisionId,
            observedUtc,
            request.PlaybookId?.Trim() ?? string.Empty,
            request.SimulationId?.Trim() ?? string.Empty,
            request.ActionTaken?.Trim() ?? string.Empty,
            request.TargetRepo?.Trim() ?? string.Empty,
            request.TargetRoute?.Trim() ?? string.Empty,
            triggerArtifacts,
            request.ResultRunId?.Trim() ?? string.Empty,
            request.ResultState?.Trim() ?? string.Empty,
            request.SuccessFlag,
            request.FailureClass?.Trim() ?? string.Empty,
            resultArtifacts,
            BuildDecisionSummary(request, triggerArtifacts, resultArtifacts),
            request.SimulationScenario?.Trim() ?? string.Empty,
            request.PredictedOutcome?.Trim() ?? string.Empty,
            request.PredictedOutcomeClass?.Trim() ?? string.Empty,
            request.PredictedConfidenceLevel?.Trim() ?? string.Empty,
            request.PredictedConfidenceScore,
            request.ActiveSignalProfileId?.Trim() ?? string.Empty,
            request.ProfileOverrideHash?.Trim() ?? string.Empty,
            request.CalibrationSnapshotLink?.Trim() ?? string.Empty,
            request.PatternEntryId?.Trim() ?? string.Empty,
            request.PatternMatchId?.Trim() ?? string.Empty,
            request.PatternLibrarySnapshotId?.Trim() ?? string.Empty,
            request.PatchCandidateId?.Trim() ?? string.Empty,
            request.PatchProvenanceId?.Trim() ?? string.Empty);
    }

    private static string BuildDecisionSummary(
        BuilderOperatorDecisionRequest request,
        IReadOnlyList<string> triggerArtifacts,
        IReadOnlyList<string> resultArtifacts)
    {
        var action = FormatValue(request.ActionTaken);
        var route = FormatValue(request.TargetRoute);
        var resultState = FormatValue(request.ResultState);
        var repo = FormatValue(request.TargetRepo);
        var triggerCount = triggerArtifacts.Count;
        var resultCount = resultArtifacts.Count;
        var prediction = string.IsNullOrWhiteSpace(request.PredictedOutcomeClass)
            ? "Prediction snapshot: not recorded."
            : $"Prediction snapshot: {FormatValue(request.PredictedOutcomeClass)} at {request.PredictedConfidenceScore:P0} confidence.";
        var signalProfile = string.IsNullOrWhiteSpace(request.ActiveSignalProfileId)
            ? "Signal profile snapshot: not recorded."
            : $"Signal profile snapshot: {FormatValue(request.ActiveSignalProfileId)} ({FormatValue(request.ProfileOverrideHash)}).";
        var patternReference = string.IsNullOrWhiteSpace(request.PatternEntryId)
            ? "Approved pattern reference: not recorded."
            : $"Approved pattern reference: {FormatValue(request.PatternEntryId)} ({FormatValue(request.PatternMatchId)}).";
        var patchCandidate = string.IsNullOrWhiteSpace(request.PatchCandidateId)
            ? "Synthesized patch candidate: not recorded."
            : $"Synthesized patch candidate: {FormatValue(request.PatchCandidateId)} ({FormatValue(request.PatchProvenanceId)}).";
        return $"{action} in {repo} on route {route} ended as {resultState}. {prediction} {signalProfile} {patternReference} {patchCandidate} Trigger artifacts: {triggerCount}. Result artifacts: {resultCount}.";
    }

    private static string BuildSummary(string workspaceId, IReadOnlyList<BuilderOperatorDecisionRecord> decisions)
        => decisions.Count == 0
            ? $"No operator decisions are currently recorded for {workspaceId}."
            : $"Recorded {decisions.Count} operator decision(s) for {workspaceId}. Latest outcome: {FormatValue(decisions[^1].ResultState)} after {FormatValue(decisions[^1].ActionTaken)}.";

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');

    private static string ComputeDeterministicId(params string[] values)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"decision-{hash[..10]}";
    }

    private static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            lock (GetSaveLock(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream);
            }
        }
        catch
        {
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (GetSaveLock(path))
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, SerializerOptions);
        }
    }

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());
}
