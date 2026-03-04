# Builder Smoke Fixture Contract

This document defines the deterministic fixture contract for the runner smoke scenario.

## Fixture root

The fixture content is scoped to:

- `etc/fixtures/builder_smoke/project/**`

Its integrity is frozen by:

- `etc/fixtures/builder_smoke/fixture.sha256`

## Allowed content policy

Keep fixture content minimal and text-first for deterministic reviews.

Forbidden file patterns are blocked by `scripts/verify_fixture_integrity.sh`, including:

- `*.pdb`, `*.exe`, `*.dll`, `*.obj`, `*.o`, `*.so`, `*.dylib`, `*.class`, `*.jar`
- `.DS_Store`, `Thumbs.db`
- `*.tmp`, `*.bak`, `*~`
- `node_modules/**`

## Verification and CI behavior

Run:

```bash
bash scripts/verify_fixture_integrity.sh
```

On drift, the script fails and writes diagnostics under:

- `artifacts/smoke/fixture_integrity/actual.sha256`
- `artifacts/smoke/fixture_integrity/actual_manifest.tsv`
- `artifacts/smoke/fixture_integrity/diff_hint.txt`

CI artifact upload includes `artifacts/**`, so these diagnostics are retained automatically.

## Intentional fixture update procedure

Fixture hash updates are explicitly gated. To refresh after intentional fixture changes:

```bash
ALLOW_FIXTURE_UPDATE=1 bash scripts/update_fixture_integrity.sh
```

Without `ALLOW_FIXTURE_UPDATE=1`, the script exits non-zero.

## Review checklist for fixture updates

When `fixture.sha256` changes, reviewers should verify:

1. Fixture content change was intentional and scoped to `etc/fixtures/builder_smoke/project/**`.
2. No forbidden patterns/files were introduced.
3. Determinism scripts still pass (`smoke_runner`, `verify_hash_contract`, `verify_fixture_integrity`, `verify_trace_schema`, `verify_trace_contract`, `replay_runner`).
4. The change rationale is documented in the PR.
