namespace Shoots.Runtime.Abstractions;

public interface IRuntimeAssist
{
    IRuntimeNarrator Narrator { get; }
    IRuntimeHelper Helper { get; }
}
