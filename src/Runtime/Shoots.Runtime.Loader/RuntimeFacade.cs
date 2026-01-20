using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader;

public sealed class RuntimeFacade : IRuntimeFacade
{
    private readonly IRuntimeHost _host;
    private readonly RuntimeCommandSurface _commands;
    private RoutingTrace? _traceSnapshot;

    public RuntimeFacade(IRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _commands = new RuntimeCommandSurface(this, _host);
    }

    public IRuntimeCommandSurface Commands => _commands;

    public IRuntimeStatusSnapshot GetStatusSnapshot() => new RuntimeStatusSnapshot(_host.Version);

    public RoutingTrace? GetTraceSnapshot() => _traceSnapshot;

    private void UpdateTraceSnapshot(RoutingTrace? trace) => _traceSnapshot = trace;

    private sealed class RuntimeCommandSurface : IRuntimeCommandSurface
    {
        private readonly RuntimeFacade _facade;

        public RuntimeCommandSurface(RuntimeFacade facade, IRuntimeHost host)
        {
            _facade = facade;
            _ = host;
        }

        public RuntimeResult Run(BuildPlan plan, CancellationToken ct = default)
        {
            _ = plan;
            _ = ct;
            _facade.UpdateTraceSnapshot(null);
            return RuntimeResult.Fail(RuntimeError.Internal("Runtime facade execution is not configured."));
        }

        public RuntimeResult Replay(RoutingTrace trace, CancellationToken ct = default)
        {
            _ = trace;
            _ = ct;
            _facade.UpdateTraceSnapshot(null);
            return RuntimeResult.Fail(RuntimeError.Internal("Runtime facade replay is not configured."));
        }

        public ToolCatalogSnapshot LoadCatalog()
        {
            return new ToolCatalogSnapshot("unavailable", Array.Empty<ToolRegistryEntry>());
        }
    }

    private sealed record RuntimeStatusSnapshot(RuntimeVersion Version) : IRuntimeStatusSnapshot;
}
