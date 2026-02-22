#!/usr/bin/env bash
set -euo pipefail

if rg -n "dotnet test" scripts build.sh build.ps1 test.sh test.ps1 tools/codex/test.sh   --glob "*.sh" --glob "*.ps1"   | rg -v "timeout|Wait-Job -Job|Invoke-DotNetTestWithTimeout|--blame-hang-timeout" >/dev/null; then
  echo "error: found dotnet test invocations without timeout guard" >&2
  rg -n "dotnet test" scripts build.sh build.ps1 test.sh test.ps1 tools/codex/test.sh     --glob "*.sh" --glob "*.ps1"     | rg -v "timeout|Wait-Job -Job|Invoke-DotNetTestWithTimeout|--blame-hang-timeout" >&2 || true
  exit 1
fi

echo "no-spin timeout guard passed"
