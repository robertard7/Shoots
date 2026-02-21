#!/usr/bin/env bash
set -euo pipefail

timeout 10m dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1
