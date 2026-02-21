#!/usr/bin/env bash
set -euo pipefail

for forbidden in src/Shoots.Provider src/Shoots.Engine; do
  if [ -e "$forbidden" ]; then
    echo "error: forbidden in-repo duplicate topology path: $forbidden" >&2
    exit 1
  fi
done

if ! rg -n "not.*Shoots\.Provider|separate .*Shoots\.Provider" src/ProviderAdapters/README.md -i >/dev/null; then
  echo "error: src/ProviderAdapters/README.md must include Shoots.Provider boundary disclaimer" >&2
  exit 1
fi

if rg -n "src/(Shoots\.Provider|Shoots\.Engine)" --glob "*.sln" >/dev/null; then
  echo "error: solution files must not reference src/Shoots.Provider or src/Shoots.Engine" >&2
  exit 1
fi

echo "repo topology guard passed"
