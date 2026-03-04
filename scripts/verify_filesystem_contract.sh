#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_filesystem_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.filesystem_contract.run_dir_missing: $run_dir" >&2; exit 64; }

repo_root="$(git rev-parse --show-toplevel)"
allowed_roots=(
  "$repo_root/artifacts"
  "$repo_root/.state"
  "$repo_root/runs"
)

write_count=0
while IFS= read -r file_path; do
  [[ -n "$file_path" ]] || continue
  abs_path="$(realpath "$file_path")"
  write_count=$((write_count + 1))

  allowed=0
  for allowed_root in "${allowed_roots[@]}"; do
    if [[ "$abs_path" == "$allowed_root"/* ]]; then
      allowed=1
      break
    fi
  done

  if [[ $allowed -eq 0 ]]; then
    echo "verify.filesystem_contract.path_outside_allowed_roots: $file_path" >&2
    exit 1
  fi
done < <(find "$run_dir" -type f | sort)

echo "FILESYSTEM_CONTRACT_OK=1"
echo "WRITE_COUNT=$write_count"
echo "FILESYSTEM_RUN_DIR=$run_dir"
