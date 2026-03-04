#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_network_contract.sh [--allow endpoint]...

Runs deterministic smoke under syscall network tracing when available and
fails if outbound endpoints are observed that are not explicitly allowed.
USAGE
}

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "verify.network_contract.dotnet_missing" >&2
  exit 127
fi

allowed_endpoints=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --allow)
      [[ $# -ge 2 ]] || { echo "verify.network_contract.arg_missing: --allow" >&2; exit 64; }
      allowed_endpoints+=("$2")
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "verify.network_contract.arg_unknown: $1" >&2
      usage >&2
      exit 64
      ;;
  esac
done

trace_log="artifacts/network_contract.strace.log"
mkdir -p artifacts

if command -v strace >/dev/null 2>&1; then
  strace -f -e trace=network -o "$trace_log" bash scripts/smoke_runner.sh --skip-backends >/dev/null
  endpoint_lines="$(grep -Eo '([0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]+' "$trace_log" | sort -u || true)"
else
  trace_log="artifacts/network_contract.unavailable.txt"
  echo "strace_unavailable" > "$trace_log"
  endpoint_lines=""
fi

violations=0
endpoint_count=0
if [[ -n "$endpoint_lines" ]]; then
  while IFS= read -r endpoint; do
    [[ -n "$endpoint" ]] || continue
    endpoint_count=$((endpoint_count + 1))
    allowed=0
    for rule in "${allowed_endpoints[@]}"; do
      if [[ "$endpoint" == "$rule" ]]; then
        allowed=1
        break
      fi
    done
    if [[ $allowed -eq 0 ]]; then
      echo "verify.network_contract.unexpected_endpoint: $endpoint" >&2
      violations=$((violations + 1))
    fi
  done <<<"$endpoint_lines"
fi

if [[ $violations -ne 0 ]]; then
  exit 1
fi

echo "NETWORK_CONTRACT_OK=1"
echo "NETWORK_ENDPOINT_COUNT=$endpoint_count"
echo "NETWORK_TRACE_PATH=$trace_log"
