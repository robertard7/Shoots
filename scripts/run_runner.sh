#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi


exec dotnet run --project src/Runtime/Shoots.Runtime.Runner/Shoots.Runtime.Runner.csproj -c Debug -- "$@"
