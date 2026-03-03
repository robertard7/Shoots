Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host 'Checking dotnet --info'
dotnet --info

Write-Host 'Checking git --version'
git --version

Write-Host 'Checking pwsh -v'
pwsh -v

Write-Host 'Runner health check passed.'
