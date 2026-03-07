Param(
    [Parameter(Mandatory = $true)]
    [string]$RunPath
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $RunPath)) {
    throw "RunPath not found: $RunPath"
}

$manifestPath = Join-Path $RunPath "artifacts\manifest.json"
$runJsonPath = Join-Path $RunPath "run.json"

if (!(Test-Path $runJsonPath)) {
    throw "run.json missing: $runJsonPath"
}

if (!(Test-Path $manifestPath)) {
    throw "manifest.json missing: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$drift = @()

foreach ($entry in $manifest.files) {
    $path = $entry.path
    if (!(Test-Path $path)) {
        $drift += "missing:$path"
        continue
    }

    $actualHash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualBytes = (Get-Item $path).Length

    if ($actualHash -ne $entry.sha256.ToLowerInvariant()) {
        $drift += "hash:$path"
    }

    if ($actualBytes -ne [int64]$entry.bytes) {
        $drift += "size:$path"
    }
}

if ($drift.Count -eq 0) {
    Write-Host "MATCH"
    exit 0
}

Write-Host "DRIFT"
$drift | ForEach-Object { Write-Host $_ }
exit 1
