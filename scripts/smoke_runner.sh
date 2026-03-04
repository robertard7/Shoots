#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/smoke_runner.sh [options]

Options:
  --skip-backends      Skip backend smoke checks
  --ollama <url>       Ollama URL override for backend smoke
  --qdrant <url>       Qdrant URL override for backend smoke
  --timeout-secs <n>   Backend smoke timeout seconds
  -h, --help           Show this help
USAGE
}

SKIP_BACKENDS=0
OLLAMA_OVERRIDE=""
QDRANT_OVERRIDE=""
TIMEOUT_OVERRIDE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-backends)
      SKIP_BACKENDS=1
      shift
      ;;
    --ollama)
      [[ $# -ge 2 ]] || { echo "smoke.runner.arg_missing: --ollama" >&2; exit 64; }
      OLLAMA_OVERRIDE="$2"
      shift 2
      ;;
    --qdrant)
      [[ $# -ge 2 ]] || { echo "smoke.runner.arg_missing: --qdrant" >&2; exit 64; }
      QDRANT_OVERRIDE="$2"
      shift 2
      ;;
    --timeout-secs)
      [[ $# -ge 2 ]] || { echo "smoke.runner.arg_missing: --timeout-secs" >&2; exit 64; }
      TIMEOUT_OVERRIDE="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "smoke.runner.arg_unknown: $1" >&2
      usage >&2
      exit 64
      ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "smoke.runner.dotnet_missing: dotnet is required" >&2
  exit 127
fi

root="$(git rev-parse --show-toplevel)"
cd "$root"

if [[ "$SKIP_BACKENDS" -eq 0 ]]; then
  smoke_args=()
  if [[ -n "$OLLAMA_OVERRIDE" ]]; then smoke_args+=(--ollama "$OLLAMA_OVERRIDE"); fi
  if [[ -n "$QDRANT_OVERRIDE" ]]; then smoke_args+=(--qdrant "$QDRANT_OVERRIDE"); fi
  if [[ -n "$TIMEOUT_OVERRIDE" ]]; then smoke_args+=(--timeout-secs "$TIMEOUT_OVERRIDE"); fi
  bash scripts/smoke_backends.sh "${smoke_args[@]}"
fi

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
out_root="artifacts/smoke/runner-${stamp}"
project_root="$out_root/project"
run_root="$out_root/run"

rm -rf "$out_root"
mkdir -p "$out_root"
cp -R etc/fixtures/builder_smoke/project "$project_root"

echo "smoke.runner.out_root=$out_root"

dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$project_root" --out "$run_root" >"$out_root/runner.stdout.log" 2>"$out_root/runner.stderr.log"

latest_run="$(find "$run_root" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1)"
[[ -n "$latest_run" && -d "$latest_run" ]] || { echo "smoke.runner.missing: run directory" >&2; exit 1; }

run_id="$(python - "$latest_run/run.json" <<'PY'
import json, pathlib, sys
obj=json.loads(pathlib.Path(sys.argv[1]).read_text())
print(obj.get('runId',''))
PY
)"

readarray -t manifest_fields < <(python - "$latest_run/hashes.json" <<'PY'
import json, pathlib, sys
obj=json.loads(pathlib.Path(sys.argv[1]).read_text())
print(obj.get('planHash',''))
print(obj.get('traceHash',''))
PY
)

created_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python - "$latest_run/manifest.json" "$run_id" "${manifest_fields[0]}" "${manifest_fields[1]}" "$latest_run" "$created_at" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
obj = {
    "run_id": sys.argv[2],
    "plan_sha256": sys.argv[3],
    "trace_sha256": sys.argv[4],
    "artifact_root": sys.argv[5],
    "created_at": sys.argv[6],
}
path.write_text(json.dumps(obj, indent=2) + "\n", encoding="utf-8")
PY

dotnet_info="$(dotnet --info 2>/dev/null | tr -d '\r')"
os_name="$(uname -s 2>/dev/null || echo unknown)"
kernel_name="$(uname -r 2>/dev/null || echo unknown)"
git_commit="$(git rev-parse HEAD)"
captured_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python - "$latest_run/environment.json" "$captured_at" "$git_commit" "$os_name" "$kernel_name" "$dotnet_info" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
obj = {
    "captured_at_utc": sys.argv[2],
    "git_commit": sys.argv[3],
    "os": sys.argv[4],
    "kernel": sys.argv[5],
    "dotnet_info": sys.argv[6],
}
path.write_text(json.dumps(obj, indent=2) + "\n", encoding="utf-8")
PY

required=(
  "run_summary.md"
  "result.json"
  "hashes.json"
  "manifest.json"
  "environment.json"
  "narration/events.ndjson"
  "trace/events.ndjson"
)

for rel in "${required[@]}"; do
  [[ -f "$latest_run/$rel" ]] || { echo "smoke.runner.missing: $latest_run/$rel" >&2; exit 1; }
done

hashes_sha="$(python - "$latest_run/hashes.json" <<'PY'
import hashlib, pathlib, sys
print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"

summary_file="artifacts/smoke/latest_summary.env"
mkdir -p "$(dirname "$summary_file")"
cat > "$summary_file" <<SUMMARY
SMOKE_RUNNER_OK=1
RUN_DIR=${latest_run}
RUN_ID=${run_id}
HASHES_SHA256=${hashes_sha}
SUMMARY
cp "$summary_file" "$out_root/latest_summary.env"

cat "$summary_file"
