#!/usr/bin/env bash
set -euo pipefail

exec dotnet run --project src/Runtime/Shoots.Runtime.Runner/Shoots.Runtime.Runner.csproj -c Debug -- "$@"
