# retrieval_golden

Deterministic retrieval regression fixture.

- `query.json`: canonical retrieval request inputs (query + slice filters + budgets).
- `expected/context_pack_first30.txt`: expected first 30 lines of `retrieval/context_pack.txt`.
- `expected/top_hits.ndjson`: expected top 10 retrieval hits.
- `expected/stats.json`: expected retrieval stats subset (`bytesOut`, `linesOut`, `filesOut`, `truncatedFlags`).
