using System.Collections.Generic;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpSurface
{
    string SurfaceId { get; }

    string SurfaceKind { get; }

    IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; }

    string DescribeContext();

    string DescribeCapabilities();

    string DescribeConstraints();
}
