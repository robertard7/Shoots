Param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
Set-Location $repoRoot

$uiProject = ".\ui\Shoots.Ui\Shoots.Ui.csproj"
$sentinelPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\smoke\last.json"
$uiLogPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\ui.log"

if (Test-Path $sentinelPath) {
    Remove-Item $sentinelPath -Force
}

taskkill /IM Shoots.UI.exe /F 2>$null | Out-Null

dotnet build $uiProject -c $Configuration

dotnet run --project $uiProject -c $Configuration -- --smoke create-project
if (!(Test-Path $sentinelPath)) { throw "Missing smoke sentinel after create-project." }

$proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
if (-not $proof.project_id) { throw "Sentinel missing project_id." }
if (-not (Test-Path $proof.workspace_path)) { throw "Workspace missing: $($proof.workspace_path)" }
if (-not (Test-Path (Join-Path $proof.workspace_path "project.json"))) { throw "project.json missing." }
if (-not $proof.required_folders_present) { throw "Required folders check failed." }

dotnet run --project $uiProject -c $Configuration -- --smoke run-demo
if (!(Test-Path $sentinelPath)) { throw "Missing smoke sentinel after run-demo." }

$proof = Get-Content $sentinelPath -Raw | ConvertFrom-Json
if (-not $proof.demo_run_id) { throw "Sentinel missing demo_run_id." }
if (-not $proof.run_json_exists) { throw "Sentinel indicates run.json missing." }
if (-not $proof.artifact_json_exists) { throw "Sentinel indicates artifact.json missing." }
if (-not $proof.environment_json_exists) { throw "Sentinel indicates environment.json missing." }
if (-not $proof.manifest_json_exists) { throw "Sentinel indicates manifest.json missing." }
if (-not $proof.evidence_bundle_exists) { throw "Sentinel indicates evidence_bundle.json missing." }
if (-not $proof.verification_report_exists) { throw "Sentinel indicates verification_report.json missing." }
if (-not $proof.log_artifact_exists) { throw "Sentinel indicates no .log artifact captured." }
if (-not $proof.artifact_verification_ok) { throw "Artifact verification failed: $($proof.artifact_verification_errors -join ", ")" }

$runPath = Join-Path $proof.workspace_path (Join-Path "runs" $proof.demo_run_id)
if (-not (Test-Path (Join-Path $runPath "run.json"))) { throw "run.json missing at $runPath" }
if (-not (Test-Path (Join-Path $runPath "artifact.json"))) { throw "artifact.json missing at $runPath" }
if (-not (Test-Path (Join-Path $runPath "environment.json"))) { throw "environment.json missing at $runPath" }
if (-not (Test-Path (Join-Path $runPath "artifacts\manifest.json"))) { throw "manifest.json missing at $runPath" }
if (-not (Test-Path (Join-Path $runPath "evidence_bundle.json"))) { throw "evidence_bundle.json missing at $runPath" }
if (-not (Test-Path (Join-Path $runPath "verification_report.json"))) { throw "verification_report.json missing at $runPath" }
$logArtifacts = Get-ChildItem -Path (Join-Path $runPath "artifacts") -Filter *.log -Recurse -ErrorAction SilentlyContinue
$verificationReportPath = Join-Path $runPath "verification_report.json"
$verificationReport = Get-Content $verificationReportPath -Raw | ConvertFrom-Json
if (-not $verificationReport.valid) { throw "verification_report.json indicates invalid run evidence." }
if (-not $logArtifacts -or $logArtifacts.Count -lt 1) { throw "Expected at least one log artifact under $runPath\artifacts" }

dotnet run --project $uiProject -c $Configuration -- --smoke intent "start new project"
if (!(Test-Path $sentinelPath)) { throw "Missing smoke sentinel after intent." }

Write-Host "Smoke sentinel: $sentinelPath"
Get-Content $sentinelPath

if (Test-Path $uiLogPath) {
    Write-Host "---- ui.log (tail 200) ----"
    Get-Content $uiLogPath -Tail 200
}
