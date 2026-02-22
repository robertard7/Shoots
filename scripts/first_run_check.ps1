param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'first_run_check.ps1 must be run on Windows.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "Repo root: $repoRoot"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI not found in PATH.'
}

$dotnetVersion = dotnet --version
Write-Host "dotnet version: $dotnetVersion"

$windowsDesktopTargets = Join-Path $env:ProgramFiles 'dotnet\sdk\*\Sdks\Microsoft.NET.Sdk.WindowsDesktop\targets\Microsoft.NET.Sdk.WindowsDesktop.targets'
if (-not (Get-ChildItem $windowsDesktopTargets -ErrorAction SilentlyContinue)) {
    Write-Warning 'WindowsDesktop SDK targets not found. UI tests/projects may fail until installed.'
} else {
    Write-Host 'WindowsDesktop SDK targets detected.'
}

& (Join-Path $repoRoot 'scripts/verify_provideradapter_naming.ps1')
& (Join-Path $repoRoot 'scripts/verify_versions.ps1')
& (Join-Path $repoRoot 'scripts/verify_repo_topology_guard.ps1')
& (Join-Path $repoRoot 'scripts/run_host_smoke.ps1')

Write-Host 'first_run_check passed.'
