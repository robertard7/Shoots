param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '0.10.0-host-boundary'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$uiProject = Join-Path $repoRoot 'ui/Shoots.Ui/Shoots.Ui.csproj'
$publishRoot = Join-Path $repoRoot "artifacts/ui/$Version/publish"
$zipPath = Join-Path $repoRoot "artifacts/ui/$Version/Shoots.Ui.zip"

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

Push-Location $repoRoot
try {
    dotnet publish $uiProject -c $Configuration -o $publishRoot
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath
    Write-Host "Packaged UI zip: $zipPath"
}
finally {
    Pop-Location
}
