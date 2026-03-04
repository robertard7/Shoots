#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_archive_determinism.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.archive.run_dir_missing: $run_dir" >&2; exit 64; }

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

archive_one="$tmp_dir/run1.tar.gz"
archive_two="$tmp_dir/run2.tar.gz"

create_archive() {
  local out="$1"
  tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner -C "$run_dir" -cf - . | gzip -n > "$out"
}

create_archive "$archive_one"
create_archive "$archive_two"

sha_one="$(sha256sum "$archive_one" | awk '{print $1}')"
sha_two="$(sha256sum "$archive_two" | awk '{print $1}')"

[[ "$sha_one" == "$sha_two" ]] || { echo "verify.archive.hash_mismatch: $sha_one != $sha_two" >&2; exit 1; }

echo "ARCHIVE_DETERMINISM_OK=1"
echo "ARCHIVE_SHA256=$sha_one"
