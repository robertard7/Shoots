[CmdletBinding()]
param(
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

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repoRoot

Write-Host "[1/10] Environment sanity check"
$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE" }
$dotnetInfo = (& dotnet --info | Out-String)
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE" }
$dotnetRuntimes = (& dotnet --list-runtimes | Out-String)
if ($LASTEXITCODE -ne 0) { throw "dotnet --list-runtimes failed with exit code $LASTEXITCODE" }
$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $dotnetVersion.StartsWith("8.")) {
    throw "Expected .NET SDK 8.x, found $dotnetVersion"
}
if (-not $runningOnWindows) {
    throw "This gate must run on Windows."
}
$dotnetReportsWindows = $dotnetInfo -match "OS Platform:\s*Windows" -or $dotnetInfo -match "RID:\s*win"
if (-not $dotnetReportsWindows) {
    Write-Warning "dotnet --info did not report a Windows OS Platform/RID; continuing because the host platform reports Windows."
}
$hasWindowsDesktop = $dotnetInfo -match "Microsoft.WindowsDesktop.App" -or $dotnetRuntimes -match "Microsoft.WindowsDesktop.App"
if (-not $hasWindowsDesktop) {
    throw "Expected Microsoft.WindowsDesktop.App runtime in dotnet --info/--list-runtimes output."
}

Write-Host "[2/10] Clean repository"
if (Test-Path ".codex") {
    Get-ChildItem ".codex" -Directory -Filter "verify-run*" -ErrorAction SilentlyContinue | ForEach-Object {
        $staleDir = $_
        try {
            Remove-Item $staleDir.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning ("Unable to remove stale verify workspace '{0}': {1}" -f $staleDir.FullName, $_.Exception.Message)
        }
    }
}

# Keep local Codex metadata out of clean to avoid recursive path explosions.
Invoke-ExternalCommand -FilePath git -Arguments @("clean", "-xfd", "-e", ".codex/") -Description "git clean"
try {
    Invoke-ExternalCommand -FilePath dotnet -Arguments @("nuget", "locals", "all", "--clear") -Description "dotnet nuget locals"
}
catch {
    Write-Warning ("dotnet nuget locals failed; continuing with existing caches. {0}" -f $_.Exception.Message)
}

Write-Host "[3/10] Restore dependencies"
Invoke-ExternalCommand -FilePath dotnet -Arguments @("restore", "Shoots.sln") -Description "dotnet restore Shoots.sln"

Write-Host "[4/10] Compile solution (Debug/Release)"
Invoke-ExternalCommand -FilePath dotnet -Arguments @("build", "Shoots.sln", "-c", "Debug", "-v", "minimal") -Description "dotnet build Debug"
Invoke-ExternalCommand -FilePath dotnet -Arguments @("build", "Shoots.sln", "-c", "Release", "-v", "minimal") -Description "dotnet build Release"

Write-Host "[5/10] Compile UI project"
Invoke-ExternalCommand -FilePath dotnet -Arguments @("build", "ui/Shoots.Ui/Shoots.Ui.csproj", "-c", "Debug") -Description "dotnet build Shoots.Ui"

Write-Host "[6/10] Run UI tests"
Invoke-ExternalCommand -FilePath dotnet -Arguments @("test", "ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj", "-c", "Debug") -Description "dotnet test Shoots.Ui.Tests"

Write-Host "[7/10] UI launch verification"
$uiProcess = Start-Process dotnet -ArgumentList "run --project ui/Shoots.Ui/Shoots.Ui.csproj -c $Configuration" -PassThru
Start-Sleep -Seconds 8
if ($uiProcess.HasExited) {
    throw "UI process exited early with code $($uiProcess.ExitCode)."
}
$uiProcess | Stop-Process -Force

Write-Host "[8/10] UI smoke execution"
Invoke-ExternalCommand -FilePath powershell -Arguments @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/smoke/windows/ui_smoke.ps1", "-Configuration", $Configuration) -Description "ui smoke"

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
Invoke-ExternalCommand -FilePath powershell -Arguments @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/replay/verify_run.ps1", "-RunPath", $runPath) -Description "verify_run"

Write-Host "[10/10] Final compile/runtime integrity gate passed"
Write-Host "run_path=$runPath"
