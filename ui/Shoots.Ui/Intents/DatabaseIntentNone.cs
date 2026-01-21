namespace Shoots.UI.Intents;

public sealed record DatabaseIntentNone : IDatabaseIntent
{
    public string Name => "None";

    public string Description => "No database intent declared.";
}
