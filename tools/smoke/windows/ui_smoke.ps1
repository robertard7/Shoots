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

dotnet run --project $uiProject -c $Configuration -- --smoke intent "start new project"
if (!(Test-Path $sentinelPath)) { throw "Missing smoke sentinel after intent." }

Write-Host "Smoke sentinel: $sentinelPath"
Get-Content $sentinelPath

if (Test-Path $uiLogPath) {
    Write-Host "---- ui.log (tail 200) ----"
    Get-Content $uiLogPath -Tail 200
}
