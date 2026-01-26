using System.Collections.Generic;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.ExecutionEnvironments;

namespace Shoots.UI.AiHelp;

public sealed class ExecutionEnvironmentAiHelpSurface : IAiHelpSurface
{
    private readonly RootFsDescriptor? _rootFs;
    private readonly string? _sourceOverride;
    private readonly string _fallbackNotice;

    public ExecutionEnvironmentAiHelpSurface(
        RootFsDescriptor? rootFs,
        string? sourceOverride,
        string fallbackNotice)
    {
        _rootFs = rootFs;
        _sourceOverride = sourceOverride;
        _fallbackNotice = fallbackNotice;
    }

    public string SurfaceId => "execution-environment";

    public string SurfaceKind => "Execution Environment";

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; } = new[]
    {
        new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Execution),
        new AiIntentDescriptor(AiIntentType.Diagnose, AiIntentScope.Execution),
        new AiIntentDescriptor(AiIntentType.Suggest, AiIntentScope.Execution)
    };

    public string DescribeContext()
    {
        if (_rootFs is null)
            return "No root filesystem is selected.";

        return $"Rootfs '{_rootFs.DisplayName}' ({_rootFs.SourceType}).";
    }

    public string DescribeCapabilities()
    {
        if (_rootFs is null)
            return "Rootfs catalog is empty.";

        return $"Source: {_rootFs.DefaultUrl ?? "no default URL"}. License: {_rootFs.License}.";
    }

    public string DescribeConstraints()
    {
        if (!string.IsNullOrWhiteSpace(_sourceOverride))
            return $"Source override is set: {_sourceOverride}.";

        return string.IsNullOrWhiteSpace(_fallbackNotice)
            ? "No execution environment constraints."
            : _fallbackNotice;
    }
}
