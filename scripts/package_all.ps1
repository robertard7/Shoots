param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = dotnet msbuild (Join-Path $repoRoot 'src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj') -nologo -getProperty:Version
$releaseRoot = Join-Path $repoRoot "artifacts/release/$version"
$nugetRoot = Join-Path $releaseRoot 'nuget'
$uiRoot = Join-Path $releaseRoot 'ui'
$opsRoot = Join-Path $releaseRoot 'ops'

New-Item -ItemType Directory -Force -Path $nugetRoot | Out-Null
New-Item -ItemType Directory -Force -Path $uiRoot | Out-Null
New-Item -ItemType Directory -Force -Path $opsRoot | Out-Null

dotnet pack (Join-Path $repoRoot 'src/Contracts/Shoots.Contracts.Core/Shoots.Contracts.Core.csproj') -c $Configuration -o $nugetRoot
dotnet pack (Join-Path $repoRoot 'src/Runtime/Shoots.Runtime.Abstractions/Shoots.Runtime.Abstractions.csproj') -c $Configuration -o $nugetRoot
dotnet pack (Join-Path $repoRoot 'src/Host/Shoots.Host.Abstractions/Shoots.Host.Abstractions.csproj') -c $Configuration -o $nugetRoot
dotnet pack (Join-Path $repoRoot 'src/Client/Shoots.Client/Shoots.Client.csproj') -c $Configuration -o $nugetRoot

& (Join-Path $repoRoot 'scripts/package_ui.ps1') -Configuration $Configuration -Version $version

if (Test-Path (Join-Path $repoRoot "artifacts/ui/$version/Shoots.Ui.zip")) {
    Copy-Item -Force (Join-Path $repoRoot "artifacts/ui/$version/Shoots.Ui.zip") (Join-Path $uiRoot 'Shoots.Ui.zip')
}

if (Test-Path (Join-Path $repoRoot 'artifacts/ops')) {
    Copy-Item -Recurse -Force (Join-Path $repoRoot 'artifacts/ops/*') $opsRoot
}

$bundle = Join-Path $repoRoot "artifacts/release/$version/shoots-release-bundle.zip"
if (Test-Path $bundle) { Remove-Item -Force $bundle }
Compress-Archive -Path (Join-Path $releaseRoot '*') -DestinationPath $bundle
Write-Host "Release bundle: $bundle"
