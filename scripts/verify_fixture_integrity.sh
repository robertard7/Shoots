#!/usr/bin/env bash
set -euo pipefail

fixture_root="etc/fixtures/builder_smoke/project"
expected_file="etc/fixtures/builder_smoke/fixture.sha256"

[[ -d "$fixture_root" ]] || { echo "verify.fixture.missing_root: $fixture_root" >&2; exit 64; }
[[ -f "$expected_file" ]] || { echo "verify.fixture.missing_expected: $expected_file" >&2; exit 64; }

actual_hash="$(python - "$fixture_root" <<'PY'
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

expected_hash="$(awk '{print $1}' "$expected_file")"

if [[ "$actual_hash" != "$expected_hash" ]]; then
  echo "FIXTURE_OK=0"
  echo "FIXTURE_EXPECTED_SHA256=$expected_hash"
  echo "FIXTURE_ACTUAL_SHA256=$actual_hash"
  echo "verify.fixture.drift: run ALLOW_FIXTURE_UPDATE=1 bash scripts/update_fixture_integrity.sh to refresh intentionally" >&2
  exit 1
fi

echo "FIXTURE_OK=1"
echo "FIXTURE_EXPECTED_SHA256=$expected_hash"
echo "FIXTURE_ACTUAL_SHA256=$actual_hash"
