#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_failure_bundle_schema.sh [BUNDLE_PATH]

Defaults to artifacts/failure_bundle.tgz
USAGE
}

case "${1:-}" in
  -h|--help)
    usage
    exit 0
    ;;
esac

bundle_path="${1:-artifacts/failure_bundle.tgz}"
[[ -f "$bundle_path" ]] || { echo "verify.failure_bundle.missing: $bundle_path" >&2; exit 64; }

entries="$(tar -tzf "$bundle_path")"
required=(
  "failure_bundle/"
  "failure_bundle/run/"
  "failure_bundle/run/hashes.json"
  "failure_bundle/run/trace/events.ndjson"
)

for rel in "${required[@]}"; do
  if ! grep -Fxq "$rel" <<<"$entries"; then
    echo "verify.failure_bundle.missing_entry: $rel" >&2
    exit 1
  fi
done

echo "FAILURE_BUNDLE_SCHEMA_OK=1"
echo "FAILURE_BUNDLE_PATH=$bundle_path"
