using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullRuntimeHelper : IRuntimeHelper
{
    public static readonly NullRuntimeHelper Instance = new();

    public NullRuntimeHelper()
    {
    }

    public RuntimeResult Help(RuntimeRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        return RuntimeResult.Fail(
            RuntimeError.Internal(
                "Runtime helper is not configured"
            )
        );
    }
}
