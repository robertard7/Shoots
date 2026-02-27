$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot '..\ui\Shoots.Ui.Tests\Shoots.Ui.Tests.csproj'
$projectPath = [System.IO.Path]::GetFullPath($projectPath)

if (-not (Test-Path $projectPath)) {
    throw "missing $projectPath"
}

[xml]$doc = Get-Content -Path $projectPath
$nodes = @($doc.Project.PropertyGroup.IsTestProject)
if ($nodes.Count -eq 0) {
    throw "expected IsTestProject guard with Condition ""'`$(OS)' != 'Windows_NT'"" and value false"
}

$foundExpected = $false
foreach ($node in $nodes) {
    $condition = ($node.Condition | ForEach-Object { $_.ToString().Trim() })
    $value = ($node.'#text' | ForEach-Object { $_.ToString().Trim().ToLowerInvariant() })

    if ($condition -eq "'`$(OS)' != 'Windows_NT'" -and $value -eq 'false') {
        $foundExpected = $true
        continue
    }

    throw "invalid IsTestProject guard in ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj (Condition='$condition', Value='$value')"
}

if (-not $foundExpected) {
    throw "expected IsTestProject guard with Condition ""'`$(OS)' != 'Windows_NT'"" and value false"
}

Write-Host 'ui test project guard passed'
