#!/usr/bin/env bash
set -euo pipefail

global_json="global.json"
[[ -f "$global_json" ]] || { echo "verify.dependency_lock.global_json_missing" >&2; exit 64; }

sdk_version="$(python - "$global_json" <<'PY'
import json, pathlib, sys
obj = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
print(obj.get('sdk', {}).get('version', ''))
PY
)"

[[ -n "$sdk_version" ]] || { echo "verify.dependency_lock.sdk_version_missing" >&2; exit 1; }

if [[ "$sdk_version" != "8.0.418" ]]; then
  echo "verify.dependency_lock.sdk_version_unexpected: $sdk_version" >&2
  exit 1
fi

lock_count="$(find . -name 'packages.lock.json' -type f | wc -l | tr -d ' ')"

restore_inputs_hash="$(
  {
    echo "global.json"
    find . -maxdepth 4 \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' -o -name 'Directory.Packages.props' -o -name 'NuGet.config' \) -type f | sort
  } | while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    sha256sum "$file"
  done | sha256sum | awk '{print $1}'
)"

echo "DEPENDENCY_LOCK_OK=1"
echo "DEPENDENCY_SDK_VERSION=$sdk_version"
echo "DEPENDENCY_LOCK_FILE_COUNT=$lock_count"
echo "DEPENDENCY_RESTORE_INPUT_SHA256=$restore_inputs_hash"
