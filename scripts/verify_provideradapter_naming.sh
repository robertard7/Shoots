#!/usr/bin/env bash
set -euo pipefail

if git ls-files src/Providers | grep -q .; then
  echo "error: tracked files under src/Providers are forbidden; use src/ProviderAdapters" >&2
  exit 1
fi

if rg -n "Shoots\.Providers\." src ui docs -S; then
  echo "error: Shoots.Providers namespace references are forbidden" >&2
  exit 1
fi

echo "provider adapter naming guard passed"
