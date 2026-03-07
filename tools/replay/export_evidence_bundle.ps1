Param(
    [Parameter(Mandatory = $true)]
    [string]$RunPath,
    [string]$OutputZipPath = ""
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $RunPath)) {
    throw "RunPath not found: $RunPath"
}

if ([string]::IsNullOrWhiteSpace($OutputZipPath)) {
    $OutputZipPath = Join-Path $RunPath "evidence_bundle.zip"
}

$files = @(
    "run.json",
    "environment.json",
    "artifact.json",
    "evidence_bundle.json",
    "narrator.jsonl",
    "artifacts\manifest.json",
    "metrics.json"
)

$stageDir = Join-Path $RunPath ".evidence_stage"
if (Test-Path $stageDir) {
    Remove-Item -Recurse -Force $stageDir
}
New-Item -ItemType Directory -Path $stageDir | Out-Null

foreach ($rel in $files) {
    $src = Join-Path $RunPath $rel
    if (!(Test-Path $src)) { continue }

    $dst = Join-Path $stageDir $rel
    $dstDir = Split-Path -Parent $dst
    if (!(Test-Path $dstDir)) {
        New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
    }

    Copy-Item $src $dst -Force
}

if (Test-Path $OutputZipPath) {
    Remove-Item $OutputZipPath -Force
}

Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $OutputZipPath -Force
Remove-Item -Recurse -Force $stageDir

Write-Host "Exported evidence bundle: $OutputZipPath"
