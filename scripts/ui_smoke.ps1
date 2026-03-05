Param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$uiProject = ".\ui\Shoots.Ui\Shoots.Ui.csproj"
$recentPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\recent-projects.json"
$uiLogPath = Join-Path $env:LOCALAPPDATA "Shoots.UI\ui.log"

Write-Host "Killing existing Shoots.UI process (if present)..."
taskkill /IM Shoots.UI.exe /F 2>$null | Out-Null

Write-Host "Building UI project..."
dotnet build $uiProject -c $Configuration

Write-Host "Running UI project..."
$proc = Start-Process dotnet -ArgumentList "run --project $uiProject -c $Configuration" -PassThru
Start-Sleep -Seconds 3

Write-Host "Please click 'New Project' in the running UI now, then press Enter to continue..."
Read-Host | Out-Null

if (!(Test-Path $recentPath)) {
    throw "recent-projects.json not found at $recentPath"
}

$recent = Get-Content $recentPath -Raw | ConvertFrom-Json
if (-not $recent -or $recent.Count -eq 0) {
    throw "recent-projects.json is empty"
}

$workspacePath = $recent[0].RootPath
$projectJson = Join-Path $workspacePath "project.json"
if (!(Test-Path $projectJson)) {
    throw "project.json not found at $projectJson"
}

Write-Host "Created workspace: $workspacePath"
Write-Host "Project file: $projectJson"

if (Test-Path $uiLogPath) {
    Write-Host "---- ui.log (last 200 lines) ----"
    Get-Content $uiLogPath -Tail 200
}

if ($proc -and -not $proc.HasExited) {
    Write-Host "Stopping UI process..."
    Stop-Process -Id $proc.Id -Force
}
