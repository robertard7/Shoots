#!/usr/bin/env bash
set -euo pipefail

roots=(
  "etc/fixtures/builder_smoke/project"
  "etc/fixtures/builder_smoke_success_args/project"
  "etc/fixtures/builder_smoke_failure/project"
  "etc/fixtures/builder_smoke_invalid_kind/project"
)

files=()
for root in "${roots[@]}"; do
  [[ -d "$root" ]] || continue
  while IFS= read -r f; do
    files+=("$f")
  done < <(find "$root" -type f | sort)
done

if [[ ${#files[@]} -eq 0 ]]; then
  echo "verify.input_normalization.no_files" >&2
  exit 64
fi

python - "${files[@]}" <<'PY'
import pathlib
import sys

crlf = []
trailing = []
bom = []
for rel in sys.argv[1:]:
    p = pathlib.Path(rel)
    data = p.read_bytes()
    if data.startswith(b'\xef\xbb\xbf'):
        bom.append(str(p))
    if b'\r\n' in data:
        crlf.append(str(p))
    try:
        text = data.decode('utf-8')
    except UnicodeDecodeError:
        continue
    for i, line in enumerate(text.splitlines(), 1):
        if line.rstrip(' \t') != line:
            trailing.append(f'{p}:{i}')

if bom:
    raise SystemExit('verify.input_normalization.bom_found:' + ','.join(bom[:20]))
if crlf:
    raise SystemExit('verify.input_normalization.crlf_found:' + ','.join(crlf[:20]))
if trailing:
    raise SystemExit('verify.input_normalization.trailing_ws_found:' + ','.join(trailing[:20]))

print('INPUT_NORMALIZATION_OK=1')
PY
