$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

dotnet test src/Tools/Shoots.Tools.Linux.Tests/Shoots.Tools.Linux.Tests.csproj -c Release -m:1

dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1 --filter "EmbeddedTool"
