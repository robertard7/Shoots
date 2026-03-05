Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

& ./scripts/validate_build.ps1 -WarningsAsErrors
if ($LASTEXITCODE -ne 0) {
    throw "verify_no_warnings failed"
}

Write-Host "NO_WARNINGS_OK=1"
