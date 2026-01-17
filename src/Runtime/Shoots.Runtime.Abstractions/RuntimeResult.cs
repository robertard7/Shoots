namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeResult
{
    public bool Ok { get; init; }
    public object? Output { get; init; }
    public RuntimeError? Error { get; init; }

    public static RuntimeResult Success(object? output = null)
        => new()
        {
            Ok = true,
            Output = output,
            Error = null
        };

    public static RuntimeResult Fail(RuntimeError error)
        => new()
        {
            Ok = false,
            Output = null,
            Error = error
        };
}
