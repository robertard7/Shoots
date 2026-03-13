param(
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$validationRoot = Join-Path $repoRoot "artifacts\validation"
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null

function Invoke-LoggedStep {
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    $logPath = Join-Path $validationRoot "$Name.log"
    & $Script 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
        throw "validate_ci_parity step failed: $Name"
    }
}

Invoke-LoggedStep -Name "build-ui" -Script { dotnet build .\ui\Shoots.Ui\Shoots.Ui.csproj -c $Configuration -v minimal }
Invoke-LoggedStep -Name "test-ui" -Script { dotnet test .\ui\Shoots.Ui.Tests\Shoots.Ui.Tests.csproj -c $Configuration -v minimal }
Invoke-LoggedStep -Name "smoke-ui" -Script { powershell -File .\tools\smoke\windows\ui_smoke.ps1 -Configuration $Configuration }
Invoke-LoggedStep -Name "integrity-ui" -Script { powershell -File .\tools\verify\windows_compile_runtime_integrity.ps1 -Configuration $Configuration }
Invoke-LoggedStep -Name "validate-build" -Script { powershell -ExecutionPolicy Bypass -File .\scripts\validate_build.ps1 -Configuration $Configuration }

Write-Host "CI_VALIDATION_PARITY_OK=1"
