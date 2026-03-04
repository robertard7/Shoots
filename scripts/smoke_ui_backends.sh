#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "$0")/lib/endpoint_resolver.sh"

SKIP_QDRANT=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-qdrant)
      SKIP_QDRANT=1
      shift
      ;;
    *)
      echo "smoke.ui_backends.arg_unknown: $1" >&2
      exit 64
      ;;
  esac
done

OLLAMA_ENDPOINT="$(resolve_ollama_endpoint)"
QDRANT_ENDPOINT="$(resolve_qdrant_endpoint)"
OLLAMA_OK=0
QDRANT_OK=0

if curl -fsS --connect-timeout 3 --max-time 3 "${OLLAMA_ENDPOINT%/}/api/tags" >/dev/null; then
  OLLAMA_OK=1
fi

if [[ "$SKIP_QDRANT" -eq 1 ]]; then
  QDRANT_OK=1
else
  if curl -fsS --connect-timeout 3 --max-time 3 "${QDRANT_ENDPOINT%/}/healthz" >/dev/null; then
    QDRANT_OK=1
  fi
fi

if [[ "$OLLAMA_OK" -eq 1 && "$QDRANT_OK" -eq 1 ]]; then
  echo "UI_BACKENDS_OK=1"
else
  echo "UI_BACKENDS_OK=0"
fi

echo "OLLAMA_OK=$OLLAMA_OK"
echo "QDRANT_OK=$QDRANT_OK"
echo "OLLAMA_ENDPOINT=$OLLAMA_ENDPOINT"
echo "QDRANT_ENDPOINT=$QDRANT_ENDPOINT"

if [[ "$OLLAMA_OK" -ne 1 ]]; then
  exit 2
fi
if [[ "$QDRANT_OK" -ne 1 ]]; then
  exit 3
fi
