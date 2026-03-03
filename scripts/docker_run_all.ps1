[CmdletBinding()]
param(
    [string]$ComposeService = "shoots-runner",
    [string]$WorkDir = "/work"
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

Require-Command docker

$null = docker compose version 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "docker compose is required."
}

$projectName = (docker compose ls --format json 2>$null | ConvertFrom-Json | Select-Object -First 1 -ExpandProperty Name)
if ([string]::IsNullOrWhiteSpace($projectName)) {
    $projectName = "<unknown>"
}

$serviceJson = docker compose ps --format json $ComposeService 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($serviceJson)) {
    throw "Required service '$ComposeService' is missing from docker compose state."
}

$service = $serviceJson | ConvertFrom-Json
if ($service -is [array]) {
    $service = $service | Select-Object -First 1
}

if ($null -eq $service) {
    throw "Required service '$ComposeService' is missing from docker compose output."
}

$containerName = [string]$service.Name
$containerId = [string]$service.ID
$containerState = [string]$service.State
$containerHealth = [string]$service.Health
if ([string]::IsNullOrWhiteSpace($containerHealth)) {
    $containerHealth = "unknown"
}

Write-Host "Compose project: $projectName"
Write-Host "Service: $ComposeService"
Write-Host "Container: $containerName"
Write-Host "Container ID: $containerId"
Write-Host "Container state: $containerState"
Write-Host "Container health: $containerHealth"

if ($containerState -ne "running") {
    throw "Container for service '$ComposeService' is not running."
}

if ($containerHealth -ne "healthy" -and $containerHealth -ne "unknown") {
    throw "Container for service '$ComposeService' is not healthy."
}

$command = "cd $WorkDir && bash scripts/codex_entrypoint.sh --all"
Write-Host "Exec command: docker compose exec -T $ComposeService bash -lc \"$command\""
docker compose exec -T $ComposeService bash -lc $command
if ($LASTEXITCODE -ne 0) {
    throw "codex_entrypoint failed with exit code $LASTEXITCODE"
}

Write-Host "Artifacts:"
Write-Host "- artifacts/"
Write-Host "- artifacts/builder_loop/"
Write-Host "- artifacts/golden/"
Write-Host "- .state/runs/"
