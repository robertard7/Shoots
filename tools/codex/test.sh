#!/usr/bin/env bash
set -euo pipefail

if command -v timeout >/dev/null 2>&1; then
  timeout 15m dotnet test -c Release
else
  dotnet test -c Release
fi

