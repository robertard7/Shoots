$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

dotnet build Shoots.sln -c Release

dotnet test src/Tools/Shoots.Tools.Linux.Tests/Shoots.Tools.Linux.Tests.csproj -c Release --no-build

Write-Host 'tools smoke passed'
