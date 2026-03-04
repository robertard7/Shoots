#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "run.ui_local.dotnet_missing: dotnet is required" >&2
  exit 127
fi

source "$(dirname "$0")/lib/endpoint_resolver.sh"
export OLLAMA_HOST="$(resolve_ollama_endpoint)"
export QDRANT_URL="$(resolve_qdrant_endpoint)"

echo "OLLAMA_HOST=$OLLAMA_HOST"
echo "QDRANT_URL=$QDRANT_URL"
dotnet --info

exec dotnet run --project ui/Shoots.Ui/Shoots.Ui.csproj -c Debug
