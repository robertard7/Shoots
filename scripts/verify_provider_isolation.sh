#!/usr/bin/env bash
set -euo pipefail

if ! command -v unshare >/dev/null 2>&1; then
  echo "verify.provider_isolation.unshare_missing" >&2
  exit 64
fi

tmp_home="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_home"
}
trap cleanup EXIT

if ! unshare -n -- bash -lc "set -euo pipefail; export HOME='$tmp_home'; export TMPDIR='$tmp_home'; bash scripts/probe_provider_adapter.sh"; then
  echo "verify.provider_isolation.probe_failed" >&2
  exit 1
fi

echo "PROVIDER_ISOLATION_OK=1"
echo "PROVIDER_ISOLATION_MODE=unshare_net_temp_home"
