#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dotnet test src/Tools/Shoots.Tools.Linux.Tests/Shoots.Tools.Linux.Tests.csproj -c Release -m:1

dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1 --filter "EmbeddedTool"
