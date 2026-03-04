#!/usr/bin/env bash
set -euo pipefail

fixture_root="etc/fixtures/builder_smoke/project"
expected_file="etc/fixtures/builder_smoke/fixture.sha256"
drift_dir="artifacts/smoke/fixture_integrity"

[[ -d "$fixture_root" ]] || { echo "verify.fixture.missing_root: $fixture_root" >&2; exit 64; }
[[ -f "$expected_file" ]] || { echo "verify.fixture.missing_expected: $expected_file" >&2; exit 64; }

mkdir -p "$drift_dir"

actual_manifest_file="$drift_dir/actual_manifest.tsv"
actual_hash_file="$drift_dir/actual.sha256"
diff_hint_file="$drift_dir/diff_hint.txt"
forbidden_file="$drift_dir/forbidden_files.txt"

: > "$diff_hint_file"
: > "$forbidden_file"

python - "$fixture_root" "$actual_manifest_file" "$actual_hash_file" "$diff_hint_file" "$forbidden_file" <<'PY'
import fnmatch
import hashlib
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
manifest_path = pathlib.Path(sys.argv[2])
actual_hash_path = pathlib.Path(sys.argv[3])
diff_hint_path = pathlib.Path(sys.argv[4])
forbidden_path = pathlib.Path(sys.argv[5])

forbidden_patterns = (
    '*.pdb',
    '*.exe',
    '*.dll',
    '*.obj',
    '*.o',
    '*.so',
    '*.dylib',
    '*.class',
    '*.jar',
    '.DS_Store',
    'Thumbs.db',
    '*.tmp',
    '*.bak',
    '*~',
    'node_modules/**',
)

files = sorted(p for p in root.rglob('*') if p.is_file())
rows = []
forbidden_hits = []
for path in files:
    rel = path.relative_to(root).as_posix()
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    rows.append(f"{rel}\t{digest}")
    for pattern in forbidden_patterns:
        if fnmatch.fnmatch(rel, pattern):
            forbidden_hits.append(rel)
            break

manifest_text = "\n".join(rows)
manifest_path.write_text(manifest_text + ("\n" if rows else ""), encoding='utf-8')
actual_hash = hashlib.sha256(manifest_text.encode('utf-8')).hexdigest()
actual_hash_path.write_text(f"{actual_hash}  {root.as_posix()}\n", encoding='utf-8')

if forbidden_hits:
    forbidden_path.write_text("\n".join(forbidden_hits) + "\n", encoding='utf-8')
    diff_hint_path.write_text(
        "FIXTURE_FORBIDDEN_FILES=1\n" + "\n".join(forbidden_hits) + "\n",
        encoding='utf-8',
    )
PY

actual_hash="$(awk '{print $1}' "$actual_hash_file")"
expected_hash="$(awk '{print $1}' "$expected_file")"

if [[ -s "$forbidden_file" ]]; then
  {
    echo "FIXTURE_OK=0"
    echo "FIXTURE_FORBIDDEN_FILES=1"
    echo "FIXTURE_FORBIDDEN_LIST=$forbidden_file"
    echo "FIXTURE_DIFF_HINT=$diff_hint_file"
  }
  echo "verify.fixture.forbidden_files_detected" >&2
  cat "$forbidden_file" >&2
  exit 1
fi

if [[ "$actual_hash" != "$expected_hash" ]]; then
  {
    echo "FIXTURE_OK=0"
    echo "FIXTURE_EXPECTED_SHA256=$expected_hash"
    echo "FIXTURE_ACTUAL_SHA256=$actual_hash"
    echo "FIXTURE_ROOT=$fixture_root"
    echo "FIXTURE_ACTUAL_SNAPSHOT=$actual_hash_file"
    echo "FIXTURE_ACTUAL_MANIFEST=$actual_manifest_file"
    echo "FIXTURE_DIFF_HINT=$diff_hint_file"
    echo "update_hint=ALLOW_FIXTURE_UPDATE=1 bash scripts/update_fixture_integrity.sh"
  } | tee "$diff_hint_file"
  echo "verify.fixture.drift: run ALLOW_FIXTURE_UPDATE=1 bash scripts/update_fixture_integrity.sh to refresh intentionally" >&2
  exit 1
fi

echo "FIXTURE_OK=1"
echo "FIXTURE_EXPECTED_SHA256=$expected_hash"
echo "FIXTURE_ACTUAL_SHA256=$actual_hash"
echo "FIXTURE_ACTUAL_SNAPSHOT=$actual_hash_file"
echo "FIXTURE_ACTUAL_MANIFEST=$actual_manifest_file"
