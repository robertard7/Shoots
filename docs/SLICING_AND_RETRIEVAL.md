# SLICING AND RETRIEVAL

## Deterministic Repo Slice Contract

Shoots defines deterministic repo slicing contracts in `Shoots.Contracts.Core`:

- `RepoSliceRequest`
- `RepoSliceFile`
- `RepoSliceDecision`
- `RepoSliceResult`
- `RepoSliceStats`

Determinism rules:

1. Include/exclude globs are normalized and sorted using ordinal ordering.
2. Path separators are normalized to `/`.
3. EOL normalization uses `\n` when `normalizeEol=true`.
4. Truncation is line-boundary-first, then byte-cap truncation.
5. Slice files/flags/decision traces are sorted by ordinal ordering.
6. `inputsHash` and `sliceId` are SHA-256 hex lowercase.

## Runtime Slice Service

`Shoots.Runtime.Language.RepoSliceService` performs deterministic slicing with bounded IO.

Stable error codes:

- `slice.root.missing`
- `slice.pattern.invalid`
- `slice.read.failed`
- `slice.binary.disallowed`
- `slice.cap.exceeded`

Truncation flags emitted in results:

- `slice.truncated.line_cap`
- `slice.truncated.bytes_per_file`
- `slice.cap.exceeded.max_files`
- `slice.cap.exceeded.total_bytes`
- `slice.binary.disallowed`

## Artifact Expectations

When used in runner/build steps, the service output should be written as:

- `slice/decisions.ndjson`
- `slice/result.json` (optional in runner)

`slice/decisions.ndjson` includes one deterministic record per considered file:

- `path`
- `includeMatch`
- `excludeMatch`
- `rejectedReason`
- `size`
- `hash`
- `bytesIncluded`
- `linesIncluded`
- `truncated`

## Retrieval Contracts

Deterministic retrieval contracts in `Shoots.Contracts.Core`:

- `RetrievalQueryRequest`
- `ContextBudget`
- `RetrievalHit`
- `RetrievalScoringTrace`
- `RetrievalResult`
- `RetrievalStats`

Stable retrieval error codes:

- `retrieval.root.missing`
- `retrieval.query.empty`
- `retrieval.slice.failed:<sliceErrorCode>`
- `retrieval.rank.failed`
- `retrieval.budget.exceeded`

### Canonical budget table

| Field | Default | Override arg |
|---|---:|---|
| `ContextBudget.MaxBytes` | `120000` | `maxTotalBytes` |
| `ContextBudget.MaxLines` | `2000` | `maxLines` |
| `ContextBudget.MaxFiles` | `12` | `maxFiles` |
| `ContextBudget.MaxTokensEstimate` | `null` | `maxTokensEstimate` |
| `RetrievalQueryRequest.MaxFileBytes` | `12000` | `maxFileBytes` |
| `RetrievalQueryRequest.MaxLinesPerFile` | `400` | `maxLinesPerFile` |

Artifacts produced by retrieval runner step:

- `retrieval/request.json`
- `retrieval/result.json`
- `retrieval/stats.json`
- `retrieval/hits.ndjson`
- `retrieval/scoring.ndjson`
- `retrieval/context_pack.txt`
- `retrieval/hashes.json`

## Context Pack Stability

`retrieval/context_pack.txt` is treated as a stable operator artifact.

Format order is fixed:

1. `# Context Pack` header
2. `runId`
3. `planHash`
4. `retrievalHash`
5. all budget fields (`maxBytes`, `maxLines`, `maxFiles`, `maxTokensEstimate`)
6. blank line
7. repeated file sections sorted by retrieval ordering

Per-file section format:

- `--- file: <path>`
- `score: <fixed-point-int-score>`
- `tokensMatched: <int>`
- `tieBreak: pathHash=<sha256>;offset=<int>`
- `reason: <comma-separated reason codes, sorted>`
- numbered lines (`00001: ...`)
- `TRUNCATED: max_file_bytes` when byte truncation occurs
- `--- endfile`

## Environment Variables

Retrieval model selection (future integration):

- `SHOOTS_EMBED_MODEL` (default planned: `nomic-embed-text`)


## Refresh workflow (local-only)

Use `bash scripts/refresh_retrieval_golden.sh` only when intentional retrieval behavior changes require a golden fixture update.

Guardrails:

- Script refuses to run in CI (`CI=true` or `GITHUB_ACTIONS=true`).
- Script rewrites only `etc/fixtures/retrieval_golden/expected/*`.
- Always review produced diff and include rationale in PR notes.
