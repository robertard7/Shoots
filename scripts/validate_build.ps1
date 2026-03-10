param(
    [switch]$WarningsAsErrors,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

New-Item -ItemType Directory -Force -Path artifacts | Out-Null

$requiredSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
$phase9ReportScript = Join-Path $repoRoot "tools/verify/phase9_validation_report.ps1"

$buildResult = "not-run"
$testResult = "not-run"
$smokeResult = "not-run"
$integrityResult = "not-run"
$firstFailure = ""

function Invoke-Step {
    param(
        [string]$Phase,
        [string]$FilePath,
        [string[]]$Arguments
    )

    $logFile = "artifacts/$Phase.log"
    & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $logFile
    if ($LASTEXITCODE -ne 0) {
        $lines = Get-Content $logFile
        $first = $lines | Select-String -Pattern 'error [A-Z]{2,}[0-9]+|: error ' | Select-Object -First 1

        Add-Content artifacts/ci-first-failure.md "## validate_build failure"
        Add-Content artifacts/ci-first-failure.md "phase=$Phase"
        Add-Content artifacts/ci-first-failure.md "log=$logFile"

        if ($null -ne $first) {
            $index = [Math]::Max(0, $first.LineNumber - 11)
            $end = [Math]::Min($lines.Count - 1, $first.LineNumber + 9)
            Add-Content artifacts/ci-first-failure.md "first_error=$($first.Line)"
            Add-Content artifacts/ci-first-failure.md ($lines[$index..$end] -join [Environment]::NewLine)
        } else {
            Add-Content artifacts/ci-first-failure.md "first_error=<none-found>"
            Add-Content artifacts/ci-first-failure.md ((Get-Content $logFile -Tail 80) -join [Environment]::NewLine)
        }

        Add-Content artifacts/ci-first-failure.md ""
        throw "validate_build step failed: $Phase"
    }
}

$warnArgs = @()
if ($WarningsAsErrors) {
    $warnArgs += '/p:TreatWarningsAsErrors=true'
}

try {
    Invoke-Step -Phase "restore" -FilePath "dotnet" -Arguments @("restore", "Shoots.sln")
    Invoke-Step -Phase "build-$Configuration" -FilePath "dotnet" -Arguments (@("build", "Shoots.sln", "-c", $Configuration, "-v", "minimal", "--no-restore") + $warnArgs)
    $buildResult = "passed"

    Invoke-Step -Phase "test-$Configuration" -FilePath "dotnet" -Arguments (@("test", "Shoots.sln", "-c", $Configuration, "-v", "minimal", "--no-build", "--no-restore") + $warnArgs)
    $testResult = "passed"

    # validate_build.ps1 only executes build+test. Smoke/integrity are reported by runner workflow stages.
    $smokeResult = "runner-stage"
    $integrityResult = "runner-stage"
}
catch {
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($firstFailure)) {
        $firstFailure = $message
    }

    if ($buildResult -eq "not-run") {
        $buildResult = "failed"
    }
    elseif ($testResult -eq "not-run") {
        $testResult = "failed"
    }

    throw
}
finally {
    if (Test-Path $phase9ReportScript) {
        & powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $phase9ReportScript `
            -OutputPath ".codex/validation/phase9_validation.md" `
            -BuildResult $buildResult `
            -TestResult $testResult `
            -SmokeResult $smokeResult `
            -IntegrityResult $integrityResult `
            -FirstFailure $firstFailure
    }
}

@(
    "BUILD_OK=1"
    "TEST_OK=1"
    "SDK_VERSION=$requiredSdk"
    "COMMIT=$((git rev-parse HEAD).Trim())"
) | Set-Content artifacts/build_summary.env

Write-Host "VALIDATE_BUILD_OK=1"
Write-Host "SDK=$requiredSdk"
Write-Host "CONFIG=$Configuration"
