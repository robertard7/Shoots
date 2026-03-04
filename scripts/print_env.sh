#!/usr/bin/env bash
set -euo pipefail

OLLAMA_HOST="${OLLAMA_HOST:-http://localhost:11434}"
QDRANT_URL="${QDRANT_URL:-http://localhost:6333}"

printf 'ENV OLLAMA_HOST=%s\n' "${OLLAMA_HOST}"
printf 'ENV QDRANT_URL=%s\n' "${QDRANT_URL}"

if command -v getent >/dev/null 2>&1; then
  printf 'DNS host.docker.internal=%s\n' "$(getent hosts host.docker.internal 2>/dev/null | awk '{print $1}' | head -n 1 || true)"
else
  echo 'DNS host.docker.internal=(getent unavailable)'
fi
