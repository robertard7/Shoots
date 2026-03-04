#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi


export OLLAMA_HOST="${OLLAMA_HOST:-http://localhost:11434}"
export QDRANT_URL="${QDRANT_URL:-http://localhost:6333}"

printf 'Resolved OLLAMA_HOST=%s\n' "$OLLAMA_HOST"
printf 'Resolved QDRANT_URL=%s\n' "$QDRANT_URL"

exec dotnet run --project ui/Shoots.Ui/Shoots.Ui.csproj -c Debug
