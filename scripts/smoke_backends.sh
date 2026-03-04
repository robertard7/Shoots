#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "$0")/lib/endpoint_resolver.sh"

OLLAMA_HOST="$(resolve_ollama_endpoint)"
QDRANT_URL="${QDRANT_URL:-}"
SKIP_QDRANT=0
TIMEOUT_SECS="${SMOKE_TIMEOUT_SECS:-3}"

usage() {
  cat <<'USAGE'
Usage: scripts/smoke_backends.sh [options]

Options:
  --ollama <url>       Override Ollama base URL (default: $OLLAMA_HOST or http://localhost:11434)
  --qdrant <url>       Override Qdrant base URL (default: $QDRANT_URL)
  --skip-qdrant        Skip Qdrant probe
  --timeout-secs <n>   Curl max time in seconds (default: 3)
  -h, --help           Show this help
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --ollama)
      [[ $# -ge 2 ]] || { echo "ERROR: --ollama requires a value" >&2; exit 64; }
      OLLAMA_HOST="$2"
      shift 2
      ;;
    --qdrant)
      [[ $# -ge 2 ]] || { echo "ERROR: --qdrant requires a value" >&2; exit 64; }
      QDRANT_URL="$2"
      shift 2
      ;;
    --skip-qdrant)
      SKIP_QDRANT=1
      shift
      ;;
    --timeout-secs)
      [[ $# -ge 2 ]] || { echo "ERROR: --timeout-secs requires a value" >&2; exit 64; }
      TIMEOUT_SECS="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "ERROR: unknown argument '$1'" >&2
      usage >&2
      exit 64
      ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]] || [[ "$TIMEOUT_SECS" -le 0 ]]; then
  echo "ERROR: --timeout-secs must be a positive integer" >&2
  exit 64
fi

if ! OLLAMA_HOST="$(normalize_absolute_http_url "$OLLAMA_HOST" 2>/dev/null)"; then
  echo "ERROR: invalid OLLAMA endpoint: $OLLAMA_HOST" >&2
  exit 64
fi

if [[ -n "$QDRANT_URL" ]]; then
  if ! QDRANT_URL="$(normalize_absolute_http_url "$QDRANT_URL" 2>/dev/null)"; then
    echo "ERROR: invalid QDRANT endpoint: $QDRANT_URL" >&2
    exit 64
  fi
fi

echo "Resolved OLLAMA_HOST=${OLLAMA_HOST}"
echo "Resolved QDRANT_URL=${QDRANT_URL:-<unset>}"
if command -v getent >/dev/null 2>&1; then
  echo "DNS host.docker.internal=$(getent hosts host.docker.internal 2>/dev/null | awk '{print $1}' | head -n 1 || true)"
fi

echo "Probing Ollama: ${OLLAMA_HOST%/}/api/tags"
if ! curl -fsS --connect-timeout "$TIMEOUT_SECS" --max-time "$TIMEOUT_SECS" "${OLLAMA_HOST%/}/api/tags" >/dev/null; then
  echo "ERROR: Ollama probe failed at ${OLLAMA_HOST%/}/api/tags" >&2
  echo "ACTION: ensure Ollama is running and reachable, then set --ollama or OLLAMA_HOST accordingly." >&2
  exit 2
fi
echo "Ollama probe passed."

if [[ "$SKIP_QDRANT" -eq 1 ]]; then
  echo "Qdrant probe skipped by --skip-qdrant."
elif [[ -z "$QDRANT_URL" ]]; then
  echo "QDRANT_URL is unset; skipping Qdrant probe."
else
  echo "Probing Qdrant: ${QDRANT_URL%/}/healthz"
  if ! curl -fsS --connect-timeout "$TIMEOUT_SECS" --max-time "$TIMEOUT_SECS" "${QDRANT_URL%/}/healthz" >/dev/null; then
    echo "ERROR: Qdrant probe failed at ${QDRANT_URL%/}/healthz" >&2
    echo "ACTION: ensure Qdrant is running/reachable or use --skip-qdrant." >&2
    exit 3
  fi
  echo "Qdrant probe passed."
fi

echo "Backend smoke checks passed."
