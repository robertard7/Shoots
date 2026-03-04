#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "probe.tool_handlers.dotnet_missing" >&2
  exit 127
fi

out_root="artifacts/tool_handler_probe"
rm -rf "$out_root"
mkdir -p "$out_root"

run_once() {
  local out="$1"
  dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project etc/fixtures/builder_smoke/project --out "$out" >/dev/null
  find "$out" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1
}

run_a="$(run_once "$out_root/run_a")"
run_b="$(run_once "$out_root/run_b")"

readarray -t values < <(python - "$run_a/hashes.json" "$run_b/hashes.json" "$run_a/result.json" "$run_b/result.json" <<'PY'
import hashlib
import json
import pathlib
import sys

h1 = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
h2 = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))
r1 = pathlib.Path(sys.argv[3]).read_bytes()
r2 = pathlib.Path(sys.argv[4]).read_bytes()

if h1.get('outputManifestHash') != h2.get('outputManifestHash'):
    raise SystemExit('probe.tool_handlers.manifest_hash_mismatch')
if h1.get('traceHash') != h2.get('traceHash'):
    raise SystemExit('probe.tool_handlers.trace_hash_mismatch')
if hashlib.sha256(r1).hexdigest() != hashlib.sha256(r2).hexdigest():
    raise SystemExit('probe.tool_handlers.result_json_mismatch')

print(h1.get('outputManifestHash',''))
PY
)

echo "TOOL_HANDLER_PROBE_OK=1"
echo "TOOL_HANDLER_PROBE_HASH=${values[0]}"
echo "TOOL_HANDLER_RUN_A=$run_a"
echo "TOOL_HANDLER_RUN_B=$run_b"
