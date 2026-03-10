Param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

trap {
    Write-Error ("[FAIL] {0}" -f $_.Exception.Message)
    exit 1
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$Description
    )

    $display = if ($Description) { $Description } else { ("{0} {1}" -f $FilePath, ($Arguments -join ' ')) }
    Write-Host "--> $display"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw ("Command failed ({0}) with exit code {1}" -f $display, $exitCode)
    }
}

function Require-Path {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Message
    )

    if (-not (Test-Path $Path)) {
        throw $Message
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
Set-Location $repoRoot

$uiProject = ".\ui\Shoots.Ui\Shoots.Ui.csproj"
$demoWaitAttempts = 90
$verifyRunScript = Join-Path $repoRoot "tools\replay\verify_run.ps1"
$sentinelPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\smoke\last.json"
$uiLogPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\ui.log"
$lastSuccessfulSentinel = $null

if (Test-Path $sentinelPath) {
    Remove-Item $sentinelPath -Force
}

$runningUi = Get-Process -Name "Shoots.UI" -ErrorAction SilentlyContinue
if ($runningUi) {
    Write-Host "Stopping existing Shoots.UI.exe instances"
    foreach ($proc in $runningUi) {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
        }
        catch [System.InvalidOperationException] {
            # Process already exited between Get-Process and Stop-Process; ignore noise.
        }
    }
}

Invoke-ExternalCommand -FilePath dotnet -Arguments @("build", $uiProject, "-c", $Configuration) -Description "dotnet build Shoots.Ui"

Invoke-ExternalCommand -FilePath dotnet -Arguments @("run", "--project", $uiProject, "-c", $Configuration, "--", "--smoke", "create-project") -Description "smoke create-project"
Require-Path -Path $sentinelPath -Message "Missing smoke sentinel after create-project."

$proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
if (-not $proof.project_id) { throw "Sentinel missing project_id." }
Require-Path -Path $proof.workspace_path -Message "Workspace missing: $($proof.workspace_path)"
Require-Path -Path (Join-Path $proof.workspace_path "project.json") -Message "project.json missing."
if (-not $proof.required_folders_present) { throw "Required folders check failed." }

Invoke-ExternalCommand -FilePath dotnet -Arguments @("run", "--project", $uiProject, "-c", $Configuration, "--", "--smoke", "run-demo") -Description "smoke run-demo"
Require-Path -Path $sentinelPath -Message "Missing smoke sentinel after run-demo."

$proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
$attempts = $demoWaitAttempts
while (-not $proof.demo_run_id -and $attempts -gt 0) {
    Start-Sleep -Seconds 1
    $proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
    $attempts--
}
if (-not $proof.demo_run_id) { throw "Sentinel missing demo_run_id." }
if (-not $proof.run_json_exists) { throw "Sentinel indicates run.json missing." }
if (-not $proof.artifact_json_exists) { throw "Sentinel indicates artifact.json missing." }
if (-not $proof.environment_json_exists) { throw "Sentinel indicates environment.json missing." }
if (-not $proof.manifest_json_exists) { throw "Sentinel indicates manifest.json missing." }
if (-not $proof.evidence_bundle_exists) { throw "Sentinel indicates evidence_bundle.json missing." }
if (-not $proof.verification_report_exists) { throw "Sentinel indicates verification_report.json missing." }
if (-not $proof.operator_flow_exists) { throw "Sentinel indicates operator_flow.json missing." }
if (-not $proof.log_artifact_exists) { throw "Sentinel indicates no .log artifact captured." }
if (-not $proof.artifact_verification_ok) { throw "Artifact verification failed: $($proof.artifact_verification_errors -join ", ")" }

$runPath = Join-Path $proof.workspace_path (Join-Path "runs" $proof.demo_run_id)
$attempts = 30
while (-not (Test-Path $runPath) -and $attempts -gt 0) {
    Start-Sleep -Seconds 1
    $attempts--
}
Require-Path -Path $runPath -Message "Run path missing at $runPath"
Require-Path -Path (Join-Path $runPath "run.json") -Message "run.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "artifact.json") -Message "artifact.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "environment.json") -Message "environment.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "artifacts\manifest.json") -Message "manifest.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "evidence_bundle.json") -Message "evidence_bundle.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "verification_report.json") -Message "verification_report.json missing at $runPath"
Require-Path -Path (Join-Path $runPath "operator_flow.json") -Message "operator_flow.json missing at $runPath"
$logArtifacts = Get-ChildItem -Path (Join-Path $runPath "artifacts") -Filter *.log -Recurse -ErrorAction SilentlyContinue
$verificationReportPath = Join-Path $runPath "verification_report.json"
$verificationReport = Get-Content $verificationReportPath -Raw | ConvertFrom-Json
if (-not $verificationReport.valid) { throw "verification_report.json indicates invalid run evidence." }
Invoke-ExternalCommand -FilePath powershell -Arguments @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $verifyRunScript, "-RunPath", $runPath) -Description "verify run (direct)"
$logCount = @($logArtifacts).Count
if ($logCount -lt 1) { throw "Expected at least one log artifact under $runPath\artifacts" }
$operatorFlow = Get-Content (Join-Path $runPath "operator_flow.json") -Raw | ConvertFrom-Json
if ($operatorFlow.host_transport -ne "none") { throw "operator_flow host transport mismatch for direct run." }

Invoke-ExternalCommand -FilePath dotnet -Arguments @("run", "--project", $uiProject, "-c", $Configuration, "--", "--smoke", "run-demo-host") -Description "smoke run-demo-host"
Require-Path -Path $sentinelPath -Message "Missing smoke sentinel after run-demo-host."

$proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
$attempts = $demoWaitAttempts
while (-not $proof.demo_run_id -and $attempts -gt 0) {
    Start-Sleep -Seconds 1
    $proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
    $attempts--
}
if (-not $proof.demo_run_id) { throw "Sentinel missing demo_run_id for host run." }
$hostRunPath = Join-Path $proof.workspace_path (Join-Path "runs" $proof.demo_run_id)
$attempts = 30
while (-not (Test-Path $hostRunPath) -and $attempts -gt 0) {
    Start-Sleep -Seconds 1
    $attempts--
}
Require-Path -Path $hostRunPath -Message "Run path missing at $hostRunPath"
$hostOperatorFlow = Get-Content (Join-Path $hostRunPath "operator_flow.json") -Raw | ConvertFrom-Json
if ($hostOperatorFlow.host_transport -ne "host") { throw "operator_flow host transport mismatch for host run." }
if (-not $proof.host_response_metadata_exists) { throw "Sentinel indicates missing host response metadata for host run." }
if (-not $hostOperatorFlow.host_response_outcome) { throw "operator_flow missing host response outcome for host run." }
Invoke-ExternalCommand -FilePath powershell -Arguments @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $verifyRunScript, "-RunPath", $hostRunPath) -Description "verify run (host)"
$lastSuccessfulSentinel = Get-Content $sentinelPath -Raw

$directRun = Get-Content (Join-Path $runPath "run.json") -Raw | ConvertFrom-Json
$hostRun = Get-Content (Join-Path $hostRunPath "run.json") -Raw | ConvertFrom-Json
$equivalence = [ordered]@{
    contract_marker_match = ($directRun.contractVersion -eq $hostRun.contractVersion)
    planner_source_match = ($directRun.plannerSource -eq $hostRun.plannerSource)
    plan_hash_match = ($directRun.planHash -eq $hostRun.planHash)
    provider_match = ($directRun.provider -eq $hostRun.provider)
    step_count_match = (($directRun.steps | Measure-Object).Count -eq ($hostRun.steps | Measure-Object).Count)
    step_ids_match = ((($directRun.steps | ForEach-Object { $_.stepId }) -join ',') -eq (($hostRun.steps | ForEach-Object { $_.stepId }) -join ','))
    step_status_match = ((($directRun.steps | ForEach-Object { $_.status }) -join ',') -eq (($hostRun.steps | ForEach-Object { $_.status }) -join ','))
    status_match = ($directRun.status -eq $hostRun.status)
    host_response_outcome = $hostRun.hostResponseOutcome
}
$equivalence.valid = $equivalence.contract_marker_match -and $equivalence.planner_source_match -and $equivalence.plan_hash_match -and $equivalence.provider_match -and $equivalence.step_count_match -and $equivalence.step_ids_match -and $equivalence.step_status_match -and $equivalence.status_match
$equivalencePath = Join-Path $hostRunPath "transport_equivalence.json"
$equivalence | ConvertTo-Json -Depth 10 | Set-Content $equivalencePath
if (-not $equivalence.valid) { throw "transport equivalence check failed. See $equivalencePath" }

Invoke-ExternalCommand -FilePath dotnet -Arguments @("run", "--project", $uiProject, "-c", $Configuration, "--", "--smoke", "intent", "start new project") -Description "smoke intent start new project"
Require-Path -Path $sentinelPath -Message "Missing smoke sentinel after intent."

if ($lastSuccessfulSentinel) {
    $lastSuccessfulSentinel | Set-Content $sentinelPath
}

Write-Host "Smoke sentinel: $sentinelPath"
Get-Content $sentinelPath

if (Test-Path $uiLogPath) {
    Write-Host "---- ui.log (tail 200) ----"
    Get-Content $uiLogPath -Tail 200
}
