#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "simulate.failure.dotnet_missing" >&2
  exit 127
fi

out_root="artifacts/failure_sim"
mkdir -p "$out_root"

run_once() {
  local out="$1"
  mkdir -p "$out"
  set +e
  dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project etc/fixtures/builder_smoke_failure/project --out "$out" >/dev/null 2>&1
  local code=$?
  set -e
  [[ $code -ne 0 ]] || { echo "simulate.failure.expected_nonzero" >&2; return 1; }
  find "$out" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1
}

run_a="$(run_once "$out_root/a")"
run_b="$(run_once "$out_root/b")"

[[ -n "$run_a" && -n "$run_b" ]] || { echo "simulate.failure.run_dir_missing" >&2; exit 1; }

for run in "$run_a" "$run_b"; do
  if [[ -d "$run" ]]; then
    bash scripts/collect_failure_bundle.sh "$run" >/dev/null 2>&1 || true
  fi
done

sha_a="$(sha256sum "$run_a/trace/events.ndjson" | awk '{print $1}')"
sha_b="$(sha256sum "$run_b/trace/events.ndjson" | awk '{print $1}')"
[[ "$sha_a" == "$sha_b" ]] || { echo "simulate.failure.trace_sha_mismatch: $sha_a != $sha_b" >&2; exit 1; }

echo "FAILURE_PATH_DETERMINISM_OK=1"
echo "FAILURE_TRACE_SHA256=$sha_a"
