#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/collect_failure_bundle.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "collect.bundle.run_dir_missing: $run_dir" >&2; exit 64; }

bundle_root="artifacts/failure_bundle"
bundle_file="artifacts/failure_bundle.tgz"
rm -rf "$bundle_root" "$bundle_file"
mkdir -p "$bundle_root"

cp -R "$run_dir" "$bundle_root/run"

for rel in hashes.json manifest.json environment.json trace/events.ndjson narration/events.ndjson; do
  src="$run_dir/$rel"
  if [[ -f "$src" ]]; then
    mkdir -p "$bundle_root/$(dirname "$rel")"
    cp "$src" "$bundle_root/$rel"
  fi
done

if [[ -d artifacts/smoke/fixture_integrity ]]; then
  mkdir -p "$bundle_root/fixture_integrity"
  cp -R artifacts/smoke/fixture_integrity/. "$bundle_root/fixture_integrity/"
fi

tar -czf "$bundle_file" -C artifacts failure_bundle

echo "FAILURE_BUNDLE_OK=1"
echo "FAILURE_BUNDLE_PATH=$bundle_file"
echo "FAILURE_BUNDLE_RUN_DIR=$run_dir"
