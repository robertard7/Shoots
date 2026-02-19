using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Core;

public static class DecisionDigest
{
    public static string Compute(DecisionInjectionRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            workOrderId = request.WorkOrderId,
            planHash = request.PlanHash,
            routeGateId = request.RouteGateId,
            toolId = request.ToolId,
            bindings = request.BindingsJsonCanonical
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
