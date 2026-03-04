# SLICING AND RETRIEVAL

## Deterministic Repo Slice Contract

Shoots defines deterministic repo slicing contracts in `Shoots.Contracts.Core`:

- `RepoSliceRequest`
- `RepoSliceFile`
- `RepoSliceResult`
- `RepoSliceStats`

Determinism rules:

1. Include/exclude globs are normalized and sorted using ordinal ordering.
2. Path separators are normalized to `/`.
3. EOL normalization uses `\n` when `normalizeEol=true`.
4. Truncation is line-boundary-first, then byte-cap truncation.
5. Slice files and flags are sorted by ordinal ordering.
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

- `slice/result.json`
- optional file excerpts under `slice/files/*`

## Environment Variables

Retrieval model selection (future integration):

- `SHOOTS_EMBED_MODEL` (default planned: `nomic-embed-text`)


## Retrieval Contracts

Deterministic retrieval contracts in `Shoots.Contracts.Core`:

- `RetrievalQueryRequest`
- `RetrievalHit`
- `RetrievalResult`
- `RetrievalStats`

Stable retrieval error codes:

- `retrieval.root.missing`
- `retrieval.query.empty`
- `retrieval.slice.failed:<sliceErrorCode>`
- `retrieval.rank.failed`
- `retrieval.budget.exceeded`

Artifacts produced by retrieval runner step:

- `retrieval/request.json`
- `retrieval/result.json`
- `retrieval/hits.ndjson`
- `retrieval/context_pack.txt`
- `retrieval/hashes.json`

## Context Pack Stability

`retrieval/context_pack.txt` is treated as a stable operator artifact.

Format order is fixed:

1. `# Context Pack` header
2. `runId`
3. `planHash`
4. `retrievalHash`
5. `budget.maxTotalBytes`
6. blank line
7. repeated file sections sorted by retrieval ordering

Per-file section format:

- `### file: <path>`
- `score: <fixed-point-int-score>`
- `reason: <comma-separated reason codes, sorted>`
- excerpt body

Truncation markers are stable when applied:

- `[TRUNCATED_BYTES]`
- `[TRUNCATED_LINES]`
