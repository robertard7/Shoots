using Shoots.Contracts.Core;

namespace Shoots.UI.ViewModels;

public sealed class ProviderCapabilityMatrixRow
{
    public ProviderCapabilityMatrixRow(
        ProviderKind kind,
        string planCapability,
        string executeCapability,
        string artifactCapability)
    {
        Kind = kind;
        PlanCapability = planCapability;
        ExecuteCapability = executeCapability;
        ArtifactCapability = artifactCapability;
    }

    public ProviderKind Kind { get; }

    public string PlanCapability { get; }

    public string ExecuteCapability { get; }

    public string ArtifactCapability { get; }

    public static ProviderCapabilityMatrixRow FromKind(ProviderKind kind)
    {
        var capabilities = kind switch
        {
            ProviderKind.Local => ProviderCapabilities.Plan | ProviderCapabilities.Execute | ProviderCapabilities.Artifacts,
            ProviderKind.Remote => ProviderCapabilities.Plan | ProviderCapabilities.Artifacts,
            ProviderKind.Delegated => ProviderCapabilities.Plan,
            _ => ProviderCapabilities.None
        };

        return new ProviderCapabilityMatrixRow(
            kind,
            FormatCapability(capabilities.HasFlag(ProviderCapabilities.Plan)),
            FormatCapability(capabilities.HasFlag(ProviderCapabilities.Execute)),
            FormatCapability(capabilities.HasFlag(ProviderCapabilities.Artifacts)));
    }

    private static string FormatCapability(bool enabled)
        => enabled ? "Yes" : "No";
}
