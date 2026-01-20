namespace Shoots.Runtime.Abstractions;

public enum ProviderFailureKind
{
    Transport,
    Timeout,
    InvalidOutput,
    ContractViolation,
    Unknown
}
