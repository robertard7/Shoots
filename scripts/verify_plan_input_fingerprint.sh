#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_plan_input_fingerprint.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.plan_input.run_dir_missing: $run_dir" >&2; exit 64; }

fingerprint_path="$run_dir/plan_input_fingerprint.json"

python - "$run_dir" "$fingerprint_path" <<'PY'
import hashlib
import json
import pathlib
import sys

run_dir = pathlib.Path(sys.argv[1]).resolve()
out_path = pathlib.Path(sys.argv[2])
repo = pathlib.Path('.').resolve()

fixture_roots = [
    repo / 'etc/fixtures/builder_smoke/project',
    repo / 'etc/fixtures/builder_smoke_success_args/project',
    repo / 'etc/fixtures/builder_smoke_failure/project',
    repo / 'etc/fixtures/builder_smoke_invalid_kind/project',
]

config_files = [
    repo / 'global.json',
    repo / 'etc/tools.catalog.json',
]

def hash_file(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def hash_tree(root: pathlib.Path) -> str:
    h = hashlib.sha256()
    for p in sorted(root.rglob('*')):
        if not p.is_file():
            continue
        rel = p.relative_to(root).as_posix().encode('utf-8')
        h.update(rel)
        h.update(b'\0')
        h.update(p.read_bytes())
        h.update(b'\0')
    return h.hexdigest()

fixture_hashes = {}
for root in fixture_roots:
    if root.exists():
        fixture_hashes[root.relative_to(repo).as_posix()] = hash_tree(root)

config_hashes = {}
for path in config_files:
    if path.exists():
        config_hashes[path.relative_to(repo).as_posix()] = hash_file(path)

plan_graph_hash = ''
for candidate in [
    run_dir / 'plan_graph.mmd',
    run_dir / 'plan/graph.mmd',
    run_dir / 'plan/plan_graph.mmd',
    run_dir / 'graph/plan.mmd',
]:
    if candidate.exists():
        text = candidate.read_text(encoding='utf-8').replace('\r\n', '\n').replace('\r', '\n')
        text = '\n'.join(line.rstrip() for line in text.split('\n')).strip() + '\n'
        plan_graph_hash = hashlib.sha256(text.encode('utf-8')).hexdigest()
        break

payload = {
    'fixture_inputs': fixture_hashes,
    'tool_catalog_sha256': config_hashes.get('etc/tools.catalog.json', ''),
    'config_sha256': config_hashes,
    'plan_graph_sha256': plan_graph_hash,
}

combined = hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest()
payload['plan_input_sha256'] = combined

out_path.write_text(json.dumps(payload, indent=2) + '\n', encoding='utf-8')
print('PLAN_INPUT_FINGERPRINT_OK=1')
print(f'PLAN_INPUT_SHA256={combined}')
print(f'PLAN_INPUT_FINGERPRINT_PATH={out_path}')
PY
