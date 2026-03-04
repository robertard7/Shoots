#!/usr/bin/env bash
set -euo pipefail

work_root="$(mktemp -d)"
cleanup() {
  rm -rf "$work_root"
}
trap cleanup EXIT

src_root="$(git rev-parse --show-toplevel)"
clone_root="$work_root/clone"

git clone --quiet "$src_root" "$clone_root"

pushd "$src_root" >/dev/null
current_fp="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
popd >/dev/null

pushd "$clone_root" >/dev/null
bash tools/codex/restore.sh
bash scripts/validate_determinism.sh --skip-backends
clone_fp="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
popd >/dev/null

if [[ "$current_fp" != "$clone_fp" ]]; then
  echo "bootstrap.repro.fingerprint_mismatch: $current_fp != $clone_fp" >&2
  exit 1
fi

echo "BOOTSTRAP_REPRO_OK=1"
echo "BOOTSTRAP_REPRO_FINGERPRINT=$current_fp"
