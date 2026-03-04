#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

python - <<'PY'
import pathlib, re, sys
text=pathlib.Path('docs/CODEX_COMMANDS.md').read_text(encoding='utf-8')
if 'lexical + deterministic only' not in text:
    print('verify.backend_optional.doc_missing')
    raise SystemExit(1)
print('verify.backend_optional.ok')
PY
