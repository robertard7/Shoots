#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet build Shoots.sln -c Release

dotnet test src/Tools/Shoots.Tools.Linux.Tests/Shoots.Tools.Linux.Tests.csproj -c Release --no-build

echo "tools smoke passed"
