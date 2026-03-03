#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

iface="ui/Shoots.Ui/Projects/IWorkspaceShellService.cs"
impl="ui/Shoots.Ui/Projects/WorkspaceShellService.cs"

mapfile -t methods < <(sed -nE 's/^\s*(bool|Task)\s+([A-Za-z0-9_]+)\(.*/\2/p' "$iface" | sort -u)

if [[ ${#methods[@]} -eq 0 ]]; then
  echo "verify.ui_contracts.no_methods"
  exit 1
fi

missing=()
for method in "${methods[@]}"; do
  if ! rg -n "\\b${method}\\s*\(" "$impl" >/dev/null; then
    missing+=("$method")
  fi
done

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "verify.ui_contracts.missing_impl: ${missing[*]}"
  exit 1
fi

echo "verify.ui_contracts.ok"
