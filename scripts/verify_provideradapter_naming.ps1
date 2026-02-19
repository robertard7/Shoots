$ErrorActionPreference = 'Stop'

$tracked = git ls-files src/Providers
if ($tracked) {
  throw 'tracked files under src/Providers are forbidden; use src/ProviderAdapters'
}

$matches = rg -n "Shoots\.Providers\." src ui docs -S
if ($LASTEXITCODE -eq 0 -and $matches) {
  $matches
  throw 'Shoots.Providers namespace references are forbidden'
}

Write-Host 'provider adapter naming guard passed'
