using System;

namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeError(
    string Code,
    string Message,
    object? Details = null)
{
    public RuntimeError
    {
        if (!RuntimeErrorCatalog.IsKnown(Code))
            throw new ArgumentException($"Runtime error code '{Code}' is not registered.", nameof(Code));
    }

    public static RuntimeError UnknownCommand(string commandId) =>
        new("unknown_command", $"Unknown command '{commandId}'");

    public static RuntimeError InvalidArguments(string message, object? details = null) =>
        new("invalid_arguments", message, details);

    public static RuntimeError Internal(string message, object? details = null) =>
        new("internal_error", message, details);
}
