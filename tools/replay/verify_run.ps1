Param(
    [string]$RunPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

trap {
    Write-Error ("[FAIL] {0}" -f $_.Exception.Message)
    exit 1
}

if ([string]::IsNullOrWhiteSpace($RunPath)) {
    $RunPath = $env:SHOOTS_VERIFY_RUN_PATH
}

if ([string]::IsNullOrWhiteSpace($RunPath)) {
    throw "RunPath argument missing. Provide -RunPath or set SHOOTS_VERIFY_RUN_PATH."
}

if (-not (Test-Path $RunPath)) {
    throw "RunPath not found: $RunPath"
}

$resolvedRunPath = (Resolve-Path -LiteralPath $RunPath).ProviderPath

function Resolve-RunRelativePath {
    param([Parameter(Mandatory = $true)] [string]$Value)

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return $Value
    }

    return (Join-Path $resolvedRunPath $Value)
}

$runJsonPath = Join-Path $resolvedRunPath "run.json"
$manifestPath = Join-Path $resolvedRunPath "artifacts\manifest.json"
$environmentPath = Join-Path $resolvedRunPath "environment.json"
$narratorPath = Join-Path $resolvedRunPath "narrator.jsonl"
$bundlePath = Join-Path $resolvedRunPath "evidence_bundle.json"
$operatorFlowPath = Join-Path $resolvedRunPath "operator_flow.json"
$reportPath = Join-Path $resolvedRunPath "verification_report.json"

if (-not (Test-Path $runJsonPath)) { throw "run.json missing: $runJsonPath" }

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
        $entryPath = Resolve-RunRelativePath -Value $entry.path
        if (-not (Test-Path $entryPath)) { $artifactErrors += "missing:$($entry.path)"; continue }
        $h = (Get-FileHash -Path $entryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $b = (Get-Item $entryPath).Length
        if ($h -ne $entry.sha256.ToLowerInvariant()) { $artifactErrors += "hash:$($entry.path)" }
        if ($b -ne [int64]$entry.bytes) { $artifactErrors += "size:$($entry.path)" }
    }
    $artifactsValid = ($artifactErrors.Count -eq 0)
    if (-not $artifactsValid) { $errors += $artifactErrors }
}

function Test-Hash {
    param(
        [string]$Path,
        [string]$Expected,
        [string]$Name
    )

    if (-not $Expected) { $script:errors += "$Name hash missing"; return $false }
    if (-not (Test-Path $Path)) { $script:errors += "$Name file missing"; return $false }
    $h = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $Expected.ToLowerInvariant()) { $script:errors += "$Name hash mismatch"; return $false }
    return $true
}

$environmentValid = Test-Hash -Path $environmentPath -Expected $run.environmentHash -Name "environment"
$narratorValid = Test-Hash -Path $narratorPath -Expected $run.narratorHash -Name "narrator"
$bundleValid = Test-Hash -Path $bundlePath -Expected $run.evidenceBundleHash -Name "bundle"

if ($run.transcriptHash) {
    $workspacePath = Split-Path (Split-Path $resolvedRunPath -Parent) -Parent
    $transcriptPath = Join-Path $workspacePath "notes\chat_transcript.jsonl"
    $transcriptValid = Test-Hash -Path $transcriptPath -Expected $run.transcriptHash -Name "transcript"
}

$contractValid = $true
if ($run.contractVersion) {
    $contractValid = ($run.contractVersion -eq "ui-runtime-v1")
    if (-not $contractValid) { $errors += "contract version mismatch" }
}
$operatorFlowValid = Test-Path $operatorFlowPath
if (-not $operatorFlowValid) { $errors += "operator flow missing" }

$hostResponseValid = $true
if ($run.hostTransport -eq "host") {
    $hostResponseValid = -not [string]::IsNullOrWhiteSpace($run.hostResponseOutcome)
    if (-not $hostResponseValid) { $errors += "host response metadata missing" }
}

$catalogPath = "etc/ui.tools.catalog.json"
if ((Test-Path $catalogPath) -and $run.toolCatalogHash) {
    $currentCatalogHash = (Get-FileHash -Path $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $catalogValid = ($currentCatalogHash -eq $run.toolCatalogHash.ToLowerInvariant())
    if (-not $catalogValid) { $errors += "catalog drift" }
}

$report = [ordered]@{
    valid = ($manifestValid -and $artifactsValid -and $environmentValid -and $narratorValid -and $bundleValid -and $catalogValid -and $transcriptValid -and $contractValid -and $operatorFlowValid -and $hostResponseValid)
    manifestValid = $manifestValid
    artifactsValid = $artifactsValid
    environmentValid = $environmentValid
    narratorValid = $narratorValid
    bundleValid = $bundleValid
    catalogValid = $catalogValid
    transcriptValid = $transcriptValid
    contractValid = $contractValid
    operatorFlowValid = $operatorFlowValid
    hostResponseValid = $hostResponseValid
    plannerSource = $run.plannerSource
    runtimeBridge = $run.runtimeBridge
    provider = $run.provider
    hostTransport = $run.hostTransport
    errors = $errors
}

$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
if ($report.valid) { Write-Host "VERIFIED"; exit 0 }
Write-Host "INVALID"
exit 1
