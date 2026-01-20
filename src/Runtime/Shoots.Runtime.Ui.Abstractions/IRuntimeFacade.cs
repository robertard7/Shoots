using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IRuntimeFacade
{
    IRuntimeCommandSurface Commands { get; }

    IRuntimeStatusSnapshot GetStatusSnapshot();

    RoutingTrace? GetTraceSnapshot();
}

public interface IRuntimeCommandSurface
{
    RuntimeResult Run(BuildPlan plan, CancellationToken ct = default);

    RuntimeResult Replay(RoutingTrace trace, CancellationToken ct = default);

    ToolCatalogSnapshot LoadCatalog();
}

public interface IRuntimeStatusSnapshot
{
    RuntimeVersion Version { get; }
}
