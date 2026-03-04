#!/usr/bin/env bash
set -euo pipefail

normalize_absolute_http_url() {
  local value="${1:-}"
  python - "$value" <<'PY'
import sys
from urllib.parse import urlparse

raw = (sys.argv[1] or '').strip()
if not raw:
    raise SystemExit(1)
parsed = urlparse(raw)
if parsed.scheme not in ('http', 'https') or not parsed.netloc:
    raise SystemExit(1)
path = parsed.path.rstrip('/')
out = f"{parsed.scheme}://{parsed.netloc}{path}"
if parsed.query:
    out += f"?{parsed.query}"
if parsed.fragment:
    out += f"#{parsed.fragment}"
print(out)
PY
}

resolve_with_fallbacks() {
  local env_name="$1"
  shift
  local env_value="${!env_name:-}"
  local normalized=""
  if normalized="$(normalize_absolute_http_url "$env_value" 2>/dev/null)"; then
    echo "$normalized"
    return 0
  fi

  local candidate
  for candidate in "$@"; do
    if normalized="$(normalize_absolute_http_url "$candidate" 2>/dev/null)"; then
      echo "$normalized"
      return 0
    fi
  done

  echo ""
}

resolve_ollama_endpoint() {
  resolve_with_fallbacks "OLLAMA_HOST" \
    "http://localhost:11434" \
    "http://127.0.0.1:11434" \
    "http://host.docker.internal:11434"
}

resolve_qdrant_endpoint() {
  resolve_with_fallbacks "QDRANT_URL" \
    "http://localhost:6333" \
    "http://127.0.0.1:6333" \
    "http://host.docker.internal:6333"
}
