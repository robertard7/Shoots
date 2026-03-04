$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (Get-Command docker -ErrorAction SilentlyContinue) {
  docker info *> $null
}

$exitCode = 0
try {
  if (Get-Command wsl -ErrorAction SilentlyContinue) {
    wsl bash -lc "cd '$repoRoot' && bash scripts/codex_ci_smoke.sh"
  } else {
    bash scripts/codex_ci_smoke.sh
  }
} catch {
  $exitCode = 1
}

Write-Host 'Latest failure triage:'
Write-Host '  artifacts/maintenance/failure-fingerprint.json'
Write-Host '  artifacts/stubs/triage.md'
Write-Host 'Latest success run:'
if (Test-Path '.state/runs') {
  $latest = Get-ChildItem '.state/runs' -Directory | Sort-Object Name | Select-Object -Last 1
  if ($latest) { Write-Host "  $($latest.FullName)" }
}

exit $exitCode
