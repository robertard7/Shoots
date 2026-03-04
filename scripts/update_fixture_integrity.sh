#!/usr/bin/env bash
set -euo pipefail

if [[ "${ALLOW_FIXTURE_UPDATE:-0}" != "1" ]]; then
  echo "update.fixture.blocked: set ALLOW_FIXTURE_UPDATE=1 to update fixture.sha256" >&2
  exit 64
fi

fixture_root="etc/fixtures/builder_smoke/project"
expected_file="etc/fixtures/builder_smoke/fixture.sha256"

[[ -d "$fixture_root" ]] || { echo "update.fixture.missing_root: $fixture_root" >&2; exit 64; }

new_hash="$(python - "$fixture_root" <<'PY'
import hashlib, pathlib, sys
root = pathlib.Path(sys.argv[1])
files = sorted(p for p in root.rglob('*') if p.is_file())
rows = []
for path in files:
    rel = path.relative_to(root).as_posix()
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    rows.append(f"{rel}\t{digest}")
manifest = "\n".join(rows).encode('utf-8')
print(hashlib.sha256(manifest).hexdigest())
PY
)"

old_hash=""
if [[ -f "$expected_file" ]]; then
  old_hash="$(awk '{print $1}' "$expected_file")"
fi

printf '%s  %s\n' "$new_hash" "$fixture_root" > "$expected_file"

echo "FIXTURE_HASH_UPDATED=1"
echo "FIXTURE_OLD_SHA256=${old_hash:-<none>}"
echo "FIXTURE_NEW_SHA256=$new_hash"
echo "FIXTURE_FILE=$expected_file"
