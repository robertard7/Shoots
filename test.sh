#!/usr/bin/env bash
set -euo pipefail

dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1
