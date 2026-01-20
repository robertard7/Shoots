using System;
using System.Text.Json.Serialization;

namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeError
{
    public string Code { get; }
    public string Message { get; }
    public object? Details { get; }

    public string? CorrelationId { get; init; }

    [JsonIgnore]
    public Exception? Cause { get; init; }

    public RuntimeError(
        string code,
        string message,
        object? details = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("code is required", nameof(code));

        if (!RuntimeErrorCatalog.IsKnown(code))
            throw new ArgumentException(
                $"Runtime error code '{code}' is not registered.",
                nameof(code));

        Code = code;
        Message = message;
        Details = details;
    }

    public static RuntimeError UnknownCommand(string commandId) =>
        new(
            "unknown_command",
            $"Unknown command '{commandId}'");

    public static RuntimeError InvalidArguments(
        string message,
        object? details = null) =>
        new(
            "invalid_arguments",
            message,
            details);

    public static RuntimeError Internal(
        string message,
        object? details = null) =>
        new(
            "internal_error",
            message,
            details);

    public static RuntimeError Internal(
        string message,
        Exception cause) =>
        new(
            "internal_error",
            message)
        {
            Cause = cause
        };

    public static string CreateCorrelationId(int tick, string code)
    {
        if (tick < 0)
            throw new ArgumentOutOfRangeException(nameof(tick));
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("code is required", nameof(code));

        return $"{code}:{tick}";
    }
}
