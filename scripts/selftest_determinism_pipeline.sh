#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "selftest.pipeline.dotnet_missing" >&2
  exit 127
fi

first_log="$(mktemp)"
second_log="$(mktemp)"
cleanup() {
  rm -f "$first_log" "$second_log"
}
trap cleanup EXIT

bash scripts/validate_determinism.sh --skip-backends | tee "$first_log"
first_fp="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
first_replay_hash="$(awk -F= '/^TRACE_HASH=/{print $2}' "$first_log" | tail -n1)"

bash scripts/validate_determinism.sh --skip-backends | tee "$second_log"
second_fp="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
second_replay_hash="$(awk -F= '/^TRACE_HASH=/{print $2}' "$second_log" | tail -n1)"

[[ -n "$first_fp" && -n "$second_fp" ]] || { echo "selftest.pipeline.fingerprint_missing" >&2; exit 1; }
[[ "$first_fp" == "$second_fp" ]] || { echo "selftest.pipeline.fingerprint_mismatch: $first_fp != $second_fp" >&2; exit 1; }

if [[ -n "$first_replay_hash" || -n "$second_replay_hash" ]]; then
  [[ "$first_replay_hash" == "$second_replay_hash" ]] || { echo "selftest.pipeline.replay_hash_mismatch: $first_replay_hash != $second_replay_hash" >&2; exit 1; }
fi

echo "PIPELINE_SELFTEST_OK=1"
echo "PIPELINE_SELFTEST_FINGERPRINT=$first_fp"
echo "PIPELINE_SELFTEST_REPLAY_HASH=${first_replay_hash:-<none>}"
