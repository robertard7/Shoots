$ErrorActionPreference = 'Stop'

$tracked = git ls-files src/Providers
if ($tracked) {
  throw 'tracked files under src/Providers are forbidden; use src/ProviderAdapters'
}

$srcMatches = rg -n "Shoots\.Providers\." src -S
if ($LASTEXITCODE -eq 0 -and $srcMatches) {
  $srcMatches
  throw 'Shoots.Providers namespace references are forbidden under src'
}

$otherMatches = rg -n "Shoots\.Providers\." ui .github/workflows -S
if ($LASTEXITCODE -eq 0 -and $otherMatches) {
  $otherMatches
  throw 'Shoots.Providers namespace references are forbidden in ui/workflows'
}

Write-Host 'provider adapter naming guard passed'
