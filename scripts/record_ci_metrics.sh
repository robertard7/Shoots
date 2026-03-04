#!/usr/bin/env bash
set -euo pipefail

run_dir="${1:-}"
if [[ -z "$run_dir" && -f artifacts/smoke/latest_summary.env ]]; then
  # shellcheck disable=SC1091
  source artifacts/smoke/latest_summary.env
  run_dir="${RUN_DIR:-}"
fi

[[ -n "$run_dir" && -d "$run_dir" ]] || { echo "record.ci_metrics.run_dir_missing" >&2; exit 64; }

metrics_file="artifacts/ci_metrics.json"
mkdir -p "$(dirname "$metrics_file")"

restore_secs="${RESTORE_SECS:-}"
smoke_secs="${SMOKE_SECS:-}"
determinism_secs="${DETERMINISM_SECS:-}"

python - "$metrics_file" "$run_dir" "$restore_secs" "$smoke_secs" "$determinism_secs" <<'PY'
import json
import pathlib
import sys
from datetime import datetime, timezone

metrics_path = pathlib.Path(sys.argv[1])
run_dir = pathlib.Path(sys.argv[2])
restore_secs = sys.argv[3]
smoke_secs = sys.argv[4]
determinism_secs = sys.argv[5]

trace_path = run_dir / 'trace' / 'events.ndjson'
artifacts_total = 0
for p in run_dir.rglob('*'):
    if p.is_file():
        artifacts_total += p.stat().st_size

replay_hash = ''
replay_path = run_dir / 'replay.json'
if replay_path.exists():
    replay = json.loads(replay_path.read_text(encoding='utf-8'))
    replay_hash = replay.get('actualTraceHash', '')

entry = {
    'captured_at_utc': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
    'run_dir': str(run_dir),
    'trace_size_bytes': trace_path.stat().st_size if trace_path.exists() else 0,
    'artifact_total_bytes': artifacts_total,
    'restore_secs': int(restore_secs) if restore_secs.isdigit() else None,
    'smoke_secs': int(smoke_secs) if smoke_secs.isdigit() else None,
    'determinism_secs': int(determinism_secs) if determinism_secs.isdigit() else None,
    'replay_hash': replay_hash,
}

if metrics_path.exists():
    payload = json.loads(metrics_path.read_text(encoding='utf-8'))
    if not isinstance(payload, list):
        raise SystemExit('record.ci_metrics.invalid_existing_payload')
else:
    payload = []

payload.append(entry)
metrics_path.write_text(json.dumps(payload, indent=2) + '\n', encoding='utf-8')

print('CI_METRICS_OK=1')
print(f'CI_METRICS_PATH={metrics_path}')
print(f'CI_METRICS_COUNT={len(payload)}')
PY
