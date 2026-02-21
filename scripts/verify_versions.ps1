$ErrorActionPreference = 'Stop'

$projects = @(
  'src/Contracts/Shoots.Contracts.Core/Shoots.Contracts.Core.csproj',
  'src/Host/Shoots.Host.Abstractions/Shoots.Host.Abstractions.csproj',
  'src/Host/Shoots.Host.Core/Shoots.Host.Core.csproj',
  'src/Runtime/Shoots.Runtime.Abstractions/Shoots.Runtime.Abstractions.csproj',
  'src/Runtime/Shoots.Runtime.Core/Shoots.Runtime.Core.csproj'
)

$versions = @()
foreach ($project in $projects) {
  $version = dotnet msbuild $project -nologo -getProperty:Version
  Write-Host "$project => $version"
  $versions += $version
}

$first = $versions[0]
foreach ($version in $versions) {
  if ($version -ne $first) {
    throw 'version mismatch detected'
  }
}

Write-Host "version consistency check passed: $first"
