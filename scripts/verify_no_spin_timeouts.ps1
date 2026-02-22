$ErrorActionPreference = 'Stop'

$matches = rg -n "dotnet test" scripts build.sh build.ps1 test.sh test.ps1 tools/codex/test.sh --glob "*.sh" --glob "*.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'no-spin timeout guard passed'
    exit 0
}

$offenders = $matches | rg -v "timeout|Wait-Job -Job|Invoke-DotNetTestWithTimeout|--blame-hang-timeout"
if ($LASTEXITCODE -eq 0 -and $offenders) {
    Write-Error "found dotnet test invocations without timeout guard:`n$offenders"
    exit 1
}

Write-Host 'no-spin timeout guard passed'
