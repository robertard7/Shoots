#!/usr/bin/env bash
set -euo pipefail

OLLAMA_HOST="${OLLAMA_HOST:-http://localhost:11434}"
QDRANT_URL="${QDRANT_URL:-}"

if [[ -z "$QDRANT_URL" ]]; then
  QDRANT_ENABLED=0
else
  QDRANT_ENABLED=1
fi

echo "Probing Ollama: ${OLLAMA_HOST}/api/tags"
if ! curl -fsS "${OLLAMA_HOST%/}/api/tags" >/dev/null; then
  echo "ERROR: Ollama probe failed at ${OLLAMA_HOST%/}/api/tags" >&2
  echo "Hint: set OLLAMA_HOST to a reachable endpoint (for Docker Desktop often http://host.docker.internal:11434)." >&2
  exit 2
fi

echo "Ollama probe passed."

if [[ "$QDRANT_ENABLED" -eq 1 ]]; then
  echo "Probing Qdrant: ${QDRANT_URL%/}/healthz"
  if ! curl -fsS "${QDRANT_URL%/}/healthz" >/dev/null; then
    echo "ERROR: Qdrant probe failed at ${QDRANT_URL%/}/healthz" >&2
    echo "Hint: set QDRANT_URL to a reachable endpoint or leave it unset to skip Qdrant smoke." >&2
    exit 3
  fi
  echo "Qdrant probe passed."
else
  echo "QDRANT_URL is not set; skipping Qdrant probe."
fi

echo "Backend smoke checks passed."
