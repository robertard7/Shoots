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

Write-Host 'repo topology guard passed'
