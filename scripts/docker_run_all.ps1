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

$composeVersion = docker compose version 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "docker compose is required."
}

$runningState = docker compose ps --status running --services 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "docker compose services are not available. Start the stack first."
}

if (-not ($runningState -split "`n" | Where-Object { $_.Trim() -eq $ComposeService })) {
    throw "Required service '$ComposeService' is not running."
}

$command = "cd $WorkDir && bash scripts/codex_entrypoint.sh --all"
Write-Host "Executing: docker compose exec -T $ComposeService bash -lc \"$command\""
docker compose exec -T $ComposeService bash -lc $command

if ($LASTEXITCODE -ne 0) {
    throw "codex_entrypoint failed with exit code $LASTEXITCODE"
}

Write-Host "Artifacts are under the repository artifact directories mounted in the container:"
Write-Host "- artifacts/"
Write-Host "- .state/runs/"
