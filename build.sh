#!/usr/bin/env bash
set -euo pipefail

dotnet restore src/Runtime/Shoots.Runtime.sln
dotnet build src/Runtime/Shoots.Runtime.sln -c Release --no-restore
dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1
