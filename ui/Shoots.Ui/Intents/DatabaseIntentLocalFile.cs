namespace Shoots.UI.Intents;

public sealed record DatabaseIntentLocalFile : IDatabaseIntent
{
    public string Name => "Local file-based (future)";

    public string Description => "Reserve space for a future file-backed database option.";
}
