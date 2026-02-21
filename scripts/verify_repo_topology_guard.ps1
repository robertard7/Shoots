$ErrorActionPreference = 'Stop'

$forbidden = @('src/Shoots.Provider', 'src/Shoots.Engine')
foreach ($path in $forbidden) {
  if (Test-Path $path) {
    throw "forbidden in-repo duplicate topology path: $path"
  }
}

$matches = rg -n "not.*Shoots\.Provider|separate .*Shoots\.Provider" src/ProviderAdapters/README.md -i
if ($LASTEXITCODE -ne 0 -or -not $matches) {
  throw 'src/ProviderAdapters/README.md must include Shoots.Provider boundary disclaimer'
}

$slnMatches = rg -n "src/(Shoots\.Provider|Shoots\.Engine)" --glob "*.sln"
if ($LASTEXITCODE -eq 0 -and $slnMatches) {
  throw 'solution files must not reference src/Shoots.Provider or src/Shoots.Engine'
}

Write-Host 'repo topology guard passed'
