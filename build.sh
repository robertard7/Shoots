#!/usr/bin/env bash
set -euo pipefail

dotnet restore src/Runtime/Shoots.Runtime.sln
dotnet build src/Runtime/Shoots.Runtime.sln -c Release --no-restore
timeout 10m dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1
timeout 10m dotnet test src/Host/Shoots.Host.Tests/Shoots.Host.Tests.csproj -c Release -m:1
