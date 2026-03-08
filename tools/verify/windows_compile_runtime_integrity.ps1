[CmdletBinding()]
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repoRoot

Write-Host "[1/10] Environment sanity check"
$dotnetVersion = (& dotnet --version).Trim()
$dotnetInfo = & dotnet --info
if (-not $dotnetVersion.StartsWith("8.")) {
    throw "Expected .NET SDK 8.x, found $dotnetVersion"
}
if ($dotnetInfo -notmatch "RID:\s*win") {
    throw "Expected Windows runtime in dotnet --info output."
}
if ($dotnetInfo -notmatch "windowsdesktop" -and $dotnetInfo -notmatch "net8.0-windows") {
    throw "Expected windows desktop support (windowsdesktop/net8.0-windows) in dotnet --info output."
}

Write-Host "[2/10] Clean repository"
& git clean -xfd
& dotnet nuget locals all --clear

Write-Host "[3/10] Restore dependencies"
& dotnet restore Shoots.sln

Write-Host "[4/10] Compile solution (Debug/Release)"
& dotnet build Shoots.sln -c Debug -v minimal
& dotnet build Shoots.sln -c Release -v minimal

Write-Host "[5/10] Compile UI project"
& dotnet build ui/Shoots.Ui/Shoots.Ui.csproj -c Debug

Write-Host "[6/10] Run UI tests"
& dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Debug

Write-Host "[7/10] UI launch verification"
$uiProcess = Start-Process dotnet -ArgumentList "run --project ui/Shoots.Ui/Shoots.Ui.csproj -c $Configuration" -PassThru
Start-Sleep -Seconds 8
if ($uiProcess.HasExited) {
    throw "UI process exited early with code $($uiProcess.ExitCode)."
}
$uiProcess | Stop-Process -Force

Write-Host "[8/10] UI smoke execution"
& powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/smoke/windows/ui_smoke.ps1 -Configuration $Configuration

$sentinelPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\smoke\last.json"
if (-not (Test-Path $sentinelPath)) {
    throw "Missing smoke sentinel at $sentinelPath"
}

$sentinel = Get-Content $sentinelPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($sentinel.workspace_path) -or [string]::IsNullOrWhiteSpace($sentinel.demo_run_id)) {
    throw "Smoke sentinel missing workspace_path/demo_run_id."
}

$runPath = Join-Path $sentinel.workspace_path (Join-Path "runs" $sentinel.demo_run_id)
$required = @(
    "run.json",
    "environment.json",
    "artifact.json",
    "artifacts\manifest.json",
    "verification_report.json",
    "operator_flow.json",
    "transport_equivalence.json",
    "narrator.jsonl"
)
foreach ($rel in $required) {
    $path = Join-Path $runPath $rel
    if (-not (Test-Path $path)) {
        throw "Missing required smoke artifact: $path"
    }
}

Write-Host "[9/10] Evidence verification"
& powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/replay/verify_run.ps1 -RunPath $runPath

Write-Host "[10/10] Final compile/runtime integrity gate passed"
Write-Host "run_path=$runPath"
