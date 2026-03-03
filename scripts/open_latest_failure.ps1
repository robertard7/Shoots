$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$fp = Join-Path $root 'artifacts/maintenance/failure-fingerprint.json'
$summary = Get-ChildItem -Path (Join-Path $root '.state/runs') -Filter run_summary.md -Recurse -ErrorAction SilentlyContinue |
  Sort-Object FullName |
  Select-Object -Last 1
$narr = Get-ChildItem -Path (Join-Path $root '.state/runs') -Filter events.ndjson -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -like '*\narration\events.ndjson' } |
  Sort-Object FullName |
  Select-Object -Last 1

if (-not (Test-Path $fp)) {
  Write-Host "failure fingerprint not found: $fp"
} else {
  Write-Host "opening: $fp"
  notepad.exe $fp
}

if ($summary) {
  Write-Host "opening: $($summary.FullName)"
  notepad.exe $summary.FullName
} else {
  Write-Host 'run_summary.md not found under .state/runs'
}

if ($narr) {
  Write-Host "opening: $($narr.FullName)"
  notepad.exe $narr.FullName
} else {
  Write-Host 'narration/events.ndjson not found under .state/runs'
}
