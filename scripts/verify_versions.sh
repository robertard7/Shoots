#!/usr/bin/env bash
set -euo pipefail

projects=(
  src/Contracts/Shoots.Contracts.Core/Shoots.Contracts.Core.csproj
  src/Host/Shoots.Host.Abstractions/Shoots.Host.Abstractions.csproj
  src/Host/Shoots.Host.Core/Shoots.Host.Core.csproj
  src/Runtime/Shoots.Runtime.Abstractions/Shoots.Runtime.Abstractions.csproj
  src/Runtime/Shoots.Runtime.Core/Shoots.Runtime.Core.csproj
)

versions=()
for p in "${projects[@]}"; do
  v=$(dotnet msbuild "$p" -nologo -getProperty:Version)
  echo "$p => $v"
  versions+=("$v")
done

first="${versions[0]}"
for v in "${versions[@]}"; do
  if [[ "$v" != "$first" ]]; then
    echo "error: version mismatch detected" >&2
    exit 1
  fi
done

echo "version consistency check passed: $first"
