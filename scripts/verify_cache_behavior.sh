#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "verify.cache.dotnet_missing" >&2
  exit 127
fi

first_log="$(mktemp)"
second_log="$(mktemp)"
cleanup() { rm -f "$first_log" "$second_log"; }
trap cleanup EXIT

bash scripts/validate_determinism.sh --skip-backends >"$first_log"
fp1="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"

SHOOTS_CACHE_DISABLED=1 bash scripts/validate_determinism.sh --skip-backends >"$second_log"
fp2="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"

[[ -n "$fp1" && -n "$fp2" ]] || { echo "verify.cache.fingerprint_missing" >&2; exit 1; }
[[ "$fp1" == "$fp2" ]] || { echo "verify.cache.fingerprint_mismatch: $fp1 != $fp2" >&2; exit 1; }

echo "CACHE_DETERMINISM_OK=1"
