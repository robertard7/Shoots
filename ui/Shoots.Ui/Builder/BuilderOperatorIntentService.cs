using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderOperatorIntentRecord(
    string WorkspaceId,
    string SchemaVersion,
    string Intent,
    DateTimeOffset IntentTimestamp,
    bool AdvisoryOnly,
    string Summary,
    string ArtifactPath);

public static class BuilderOperatorIntentService
{
    public const string OperatorIntentFileName = "builder_operator_intent.json";
    public const string FastRecoveryIntent = "fast_recovery";
    public const string SafeRecoveryIntent = "safe_recovery";
    public const string MinimalChangeIntent = "minimal_change";
    public const string FullResolutionIntent = "full_resolution";
    public const string UnblockOrchestrationIntent = "unblock_orchestration";

    private const string SchemaVersion = "builder_operator_intent.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SupportedIntents =
    {
        FastRecoveryIntent,
        SafeRecoveryIntent,
        MinimalChangeIntent,
        FullResolutionIntent,
        UnblockOrchestrationIntent
    };

    public static string OperatorIntentPathForRepo(string repoRoot)
        => Path.Combine(BuilderWorkspaceService.WorkspaceRootForRepo(repoRoot), OperatorIntentFileName);

    public static BuilderOperatorIntentRecord? LoadOperatorIntent(string repoRoot)
        => Load<BuilderOperatorIntentRecord>(OperatorIntentPathForRepo(repoRoot));

    public static BuilderOperatorIntentRecord SetOperatorIntent(
        string repoRoot,
        string intent,
        DateTimeOffset? observedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var normalizedIntent = NormalizeIntent(intent);
        if (string.IsNullOrWhiteSpace(normalizedIntent) || !IsSupportedIntent(normalizedIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "Intent must be one of the supported deterministic operator intents.");
        }

        var artifactPath = OperatorIntentPathForRepo(repoRoot);
        var observed = observedUtc ?? DateTimeOffset.UtcNow;
        var record = new BuilderOperatorIntentRecord(
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            SchemaVersion,
            normalizedIntent,
            observed,
            true,
            $"Operator intent is {GetIntentLabel(normalizedIntent)}. This intent is advisory only and does not bypass routing, review, approval, or finalize gates.",
            artifactPath);
        Save(artifactPath, record);
        return record;
    }

    public static IReadOnlyList<string> GetSupportedIntents()
        => SupportedIntents.ToArray();

    public static bool IsSupportedIntent(string? intent)
        => SupportedIntents.Contains(NormalizeIntent(intent), StringComparer.OrdinalIgnoreCase);

    public static string GetIntentLabel(string? intent)
        => NormalizeIntent(intent) switch
        {
            FastRecoveryIntent => "Fast Recovery",
            SafeRecoveryIntent => "Safe Recovery",
            MinimalChangeIntent => "Minimal Change",
            FullResolutionIntent => "Full Resolution",
            UnblockOrchestrationIntent => "Unblock Orchestration",
            _ => "No explicit intent"
        };

    private static string NormalizeIntent(string? intent)
        => intent?.Trim() ?? string.Empty;

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
