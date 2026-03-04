#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "verify.binary_repro.dotnet_missing" >&2
  exit 127
fi

project="src/Runtime/Shoots.Runtime.Runner/Shoots.Runtime.Runner.csproj"
[[ -f "$project" ]] || { echo "verify.binary_repro.project_missing: $project" >&2; exit 64; }

work_dir="$(mktemp -d)"
cleanup() { rm -rf "$work_dir"; }
trap cleanup EXIT

out1="$work_dir/out1"
out2="$work_dir/out2"

build_once() {
  local out="$1"
  dotnet publish "$project" -c Release -o "$out" --nologo >/dev/null
  find "$out" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.exe' -o -name '*.so' -o -name '*.dylib' \) | sort
}

files1="$(build_once "$out1")"
files2="$(build_once "$out2")"

first1="$(head -n1 <<<"$files1")"
first2_name="$(basename "$(head -n1 <<<"$files2")")"
[[ -n "$first1" ]] || { echo "verify.binary_repro.no_binary_output" >&2; exit 1; }

candidate2="$out2/$first2_name"
[[ -f "$candidate2" ]] || { echo "verify.binary_repro.matching_binary_missing" >&2; exit 1; }

sha1="$(sha256sum "$first1" | awk '{print $1}')"
sha2="$(sha256sum "$candidate2" | awk '{print $1}')"
[[ "$sha1" == "$sha2" ]] || { echo "verify.binary_repro.sha_mismatch: $sha1 != $sha2" >&2; exit 1; }

echo "BINARY_REPRO_OK=1"
echo "BINARY_SHA256=$sha1"
