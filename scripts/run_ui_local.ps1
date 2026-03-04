$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path "$HOME/.dotnet/dotnet") {
        $env:PATH = "$HOME/.dotnet;$env:PATH"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'run.ui_local.dotnet_missing: dotnet is required'
    exit 127
}

$ollama = if ([string]::IsNullOrWhiteSpace($env:OLLAMA_HOST)) { 'http://localhost:11434' } else { $env:OLLAMA_HOST }
$qdrant = if ([string]::IsNullOrWhiteSpace($env:QDRANT_URL)) { 'http://localhost:6333' } else { $env:QDRANT_URL }

Write-Host "OLLAMA_HOST=$ollama"
Write-Host "QDRANT_URL=$qdrant"

$env:OLLAMA_HOST = $ollama
$env:QDRANT_URL = $qdrant

dotnet --info
dotnet run --project ui/Shoots.Ui/Shoots.Ui.csproj -c Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
