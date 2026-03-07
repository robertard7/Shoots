Param(
    [Parameter(Mandatory = $true)]
    [string]$RunPath
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $RunPath)) {
    throw "RunPath not found: $RunPath"
}

$runJsonPath = Join-Path $RunPath "run.json"
$manifestPath = Join-Path $RunPath "artifacts\manifest.json"
$environmentPath = Join-Path $RunPath "environment.json"
$narratorPath = Join-Path $RunPath "narrator.jsonl"
$bundlePath = Join-Path $RunPath "evidence_bundle.json"
$operatorFlowPath = Join-Path $RunPath "operator_flow.json"
$reportPath = Join-Path $RunPath "verification_report.json"

if (!(Test-Path $runJsonPath)) { throw "run.json missing: $runJsonPath" }

$run = Get-Content $runJsonPath -Raw | ConvertFrom-Json
$errors = @()
$manifestValid = Test-Path $manifestPath
$artifactsValid = $false
$environmentValid = $false
$narratorValid = $false
$bundleValid = $false
$catalogValid = $true
$transcriptValid = $true

if (-not $manifestValid) {
    $errors += "manifest missing"
} else {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $artifactErrors = @()
    foreach ($entry in $manifest.files) {
        if (!(Test-Path $entry.path)) { $artifactErrors += "missing:$($entry.path)"; continue }
        $h = (Get-FileHash -Path $entry.path -Algorithm SHA256).Hash.ToLowerInvariant()
        $b = (Get-Item $entry.path).Length
        if ($h -ne $entry.sha256.ToLowerInvariant()) { $artifactErrors += "hash:$($entry.path)" }
        if ($b -ne [int64]$entry.bytes) { $artifactErrors += "size:$($entry.path)" }
    }
    $artifactsValid = ($artifactErrors.Count -eq 0)
    if (-not $artifactsValid) { $errors += $artifactErrors }
}

function Test-Hash([string]$path, [string]$expected, [string]$name) {
    if (-not $expected) { $script:errors += "$name hash missing"; return $false }
    if (!(Test-Path $path)) { $script:errors += "$name file missing"; return $false }
    $h = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $expected.ToLowerInvariant()) { $script:errors += "$name hash mismatch"; return $false }
    return $true
}

$environmentValid = Test-Hash $environmentPath $run.environmentHash "environment"
$narratorValid = Test-Hash $narratorPath $run.narratorHash "narrator"
$bundleValid = Test-Hash $bundlePath $run.evidenceBundleHash "bundle"

if ($run.transcriptHash) {
    $workspacePath = Split-Path (Split-Path $RunPath -Parent) -Parent
    $transcriptPath = Join-Path $workspacePath "notes\chat_transcript.jsonl"
    $transcriptValid = Test-Hash $transcriptPath $run.transcriptHash "transcript"
}

$contractValid = $true
if ($run.contractVersion) {
    $contractValid = ($run.contractVersion -eq "ui-runtime-v1")
    if (-not $contractValid) { $errors += "contract version mismatch" }
}
$operatorFlowValid = Test-Path $operatorFlowPath
if (-not $operatorFlowValid) { $errors += "operator flow missing" }

$catalogPath = "etc/ui.tools.catalog.json"
if (Test-Path $catalogPath -and $run.toolCatalogHash) {
    $currentCatalogHash = (Get-FileHash -Path $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $catalogValid = ($currentCatalogHash -eq $run.toolCatalogHash.ToLowerInvariant())
    if (-not $catalogValid) { $errors += "catalog drift" }
}

$report = [ordered]@{
    valid = ($manifestValid -and $artifactsValid -and $environmentValid -and $narratorValid -and $bundleValid -and $catalogValid -and $transcriptValid -and $contractValid -and $operatorFlowValid)
    manifestValid = $manifestValid
    artifactsValid = $artifactsValid
    environmentValid = $environmentValid
    narratorValid = $narratorValid
    bundleValid = $bundleValid
    catalogValid = $catalogValid
    transcriptValid = $transcriptValid
    contractValid = $contractValid
    operatorFlowValid = $operatorFlowValid
    errors = $errors
}

$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
if ($report.valid) { Write-Host "VERIFIED"; exit 0 }
Write-Host "INVALID"
exit 1
