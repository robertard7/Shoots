Param(
    [Parameter(Mandatory = $true)]
    [string]$RunPath
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $RunPath)) {
    throw "RunPath not found: $RunPath"
}

$manifestPath = Join-Path $RunPath "artifacts\manifest.json"
$runJsonPath = Join-Path $RunPath "run.json"
$environmentPath = Join-Path $RunPath "environment.json"
$driftReportPath = Join-Path $RunPath "drift_report.json"

if (!(Test-Path $runJsonPath)) {
    throw "run.json missing: $runJsonPath"
}

if (!(Test-Path $manifestPath)) {
    throw "manifest.json missing: $manifestPath"
}

$run = Get-Content $runJsonPath -Raw | ConvertFrom-Json
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$missingFiles = @()
$hashMismatches = @()
$sizeMismatches = @()

foreach ($entry in $manifest.files) {
    $path = $entry.path
    if (!(Test-Path $path)) {
        $missingFiles += $path
        continue
    }

    $actualHash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualBytes = (Get-Item $path).Length

    if ($actualHash -ne $entry.sha256.ToLowerInvariant()) {
        $hashMismatches += $path
    }

    if ($actualBytes -ne [int64]$entry.bytes) {
        $sizeMismatches += $path
    }
}

$catalogMismatch = $false
$currentCatalogHash = ""
$catalogPath = "etc/ui.tools.catalog.json"
if (Test-Path $catalogPath) {
    $currentCatalogHash = (Get-FileHash -Path $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $catalogMismatch = ($run.toolCatalogHash -and $run.toolCatalogHash.ToLowerInvariant() -ne $currentCatalogHash)
}

$environmentMismatch = $false
$currentEnvironmentHash = ""
if (Test-Path $environmentPath) {
    $currentEnvironmentHash = (Get-FileHash -Path $environmentPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($run.environmentHash) {
        $environmentMismatch = ($run.environmentHash.ToLowerInvariant() -ne $currentEnvironmentHash)
    }
}

$report = [ordered]@{
    run_path = $RunPath
    missing_files = $missingFiles
    changed_hash = $hashMismatches
    changed_size = $sizeMismatches
    environment_mismatch = $environmentMismatch
    catalog_mismatch = $catalogMismatch
    current_catalog_hash = $currentCatalogHash
    current_environment_hash = $currentEnvironmentHash
}

$report | ConvertTo-Json -Depth 10 | Set-Content $driftReportPath

$hasDrift = ($missingFiles.Count -gt 0) -or ($hashMismatches.Count -gt 0) -or ($sizeMismatches.Count -gt 0) -or $environmentMismatch -or $catalogMismatch
if (-not $hasDrift) {
    Write-Host "MATCH"
    exit 0
}

Write-Host "DRIFT"
Write-Host "See drift report: $driftReportPath"
exit 1
