param(
    [string]$OutputPath = ".codex/validation/phase9_validation.md",
    [string]$BuildResult = "unknown",
    [string]$TestResult = "unknown",
    [string]$SmokeResult = "unknown",
    [string]$IntegrityResult = "unknown",
    [string]$FirstFailure = ""
)

$timestamp = [DateTimeOffset]::UtcNow.ToString("O")
$lines = @(
    "# Phase 9 Validation Report",
    "",
    "- Timestamp (UTC): $timestamp",
    "- Build: $BuildResult",
    "- Test: $TestResult",
    "- Smoke: $SmokeResult",
    "- Integrity: $IntegrityResult",
    ""
)

if (-not [string]::IsNullOrWhiteSpace($FirstFailure)) {
    $lines += "## First Failure"
    $lines += ""
    $lines += "```"
    $lines += $FirstFailure
    $lines += "```"
}

$directory = Split-Path -Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Set-Content -Path $OutputPath -Value $lines -Encoding UTF8
Write-Host "Wrote validation report to $OutputPath"
