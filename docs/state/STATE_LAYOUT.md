# State Layout

- `.state/models.catalog.json`: local model catalog cache used by Host model selection.
- `.state/` is local-only and gitignored.
- State files must not be included in digest material except explicit selected values read into request payloads.
- No secrets, chat transcripts, or external credentials are stored in `.state/`.
