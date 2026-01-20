using System;

namespace Shoots.Runtime.Abstractions;

public sealed class ProviderFailureException : Exception
{
    public ProviderFailureException(ProviderFailure failure, Exception? innerException = null)
        : base(failure?.Message, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public ProviderFailure Failure { get; }
}
