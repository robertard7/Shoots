namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpSurface
{
    string SurfaceKind { get; }

    string DescribeContext();

    string DescribeCapabilities();

    string DescribeConstraints();
}
