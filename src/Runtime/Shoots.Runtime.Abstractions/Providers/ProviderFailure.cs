using System;
using System.Net.Http;
using System.Text.Json;

namespace Shoots.Runtime.Abstractions;

public sealed record ProviderFailure(
    ProviderFailureKind Kind,
    string Message,
    string? ProviderId,
    string ExceptionType)
{
    public static ProviderFailure FromException(Exception exception, string? providerId = null)
    {
        if (exception is null)
            throw new ArgumentNullException(nameof(exception));

        if (exception is ProviderFailureException failureException)
            return failureException.Failure;

        var root = Unwrap(exception);
        var kind = ResolveKind(root);
        var message = string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message;
        var typeName = root.GetType().FullName ?? root.GetType().Name;
        return new ProviderFailure(kind, message, providerId, typeName);
    }

    private static Exception Unwrap(Exception exception)
    {
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            return aggregate.InnerExceptions[0];

        return exception.InnerException is null ? exception : exception.InnerException;
    }

    private static ProviderFailureKind ResolveKind(Exception exception)
    {
        if (exception is TimeoutException)
            return ProviderFailureKind.Timeout;
        if (exception is HttpRequestException)
            return ProviderFailureKind.Transport;
        if (exception is JsonException or FormatException)
            return ProviderFailureKind.InvalidOutput;
        if (exception is ArgumentException or InvalidOperationException)
            return ProviderFailureKind.ContractViolation;

        if (exception.InnerException is not null)
            return ResolveKind(exception.InnerException);

        return ProviderFailureKind.Unknown;
    }
}
