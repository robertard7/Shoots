#!/usr/bin/env bash
set -euo pipefail

targets=(
  "etc/fixtures/builder_smoke/project"
  "etc/fixtures/builder_smoke_success_args/project"
  "etc/fixtures/builder_smoke_failure/project"
  "etc/fixtures/builder_smoke_invalid_kind/project"
)

if [[ -f artifacts/smoke/latest_summary.env ]]; then
  # shellcheck disable=SC1091
  source artifacts/smoke/latest_summary.env
  if [[ -n "${RUN_DIR:-}" && -f "$RUN_DIR/artifact_index.json" ]]; then
    targets+=("$RUN_DIR")
  fi
fi

python - "${targets[@]}" <<'PY'
import pathlib
import sys

crlf = 0
for target in sys.argv[1:]:
    p = pathlib.Path(target)
    if not p.exists():
        continue
    files = [p] if p.is_file() else [f for f in p.rglob('*') if f.is_file()]
    for f in files:
        data = f.read_bytes()
        if b'\r\n' in data:
            crlf += 1

print('LINE_ENDINGS_OK=1')
print(f'CRLF_COUNT={crlf}')
if crlf:
    raise SystemExit('verify.line_endings.crlf_found')
PY
