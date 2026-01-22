namespace Shoots.UI.Intents;

public sealed record DatabaseIntentOption(
    DatabaseIntent Intent,
    string Name,
    string Description);
