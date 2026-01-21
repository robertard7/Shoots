namespace Shoots.UI.Intents;

public sealed record DatabaseIntentExternalService : IDatabaseIntent
{
    public string Name => "External service (future)";

    public string Description => "Reserve space for a future external database service option.";
}
