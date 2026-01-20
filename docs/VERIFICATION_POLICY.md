# Verification Policy

## Scope

This repository prohibits local execution of builds and tests by automation.
All validation must be performed by GitHub Actions on the Windows self-hosted runner.

## Local Tests

Local test execution is prohibited for automated agents. This policy exists to ensure
validation happens on the canonical runner and avoids non-reproducible environments.

## CI / Self-Hosted Runner Requirements

Verification requires GitHub Actions runs on the Windows self-hosted runner:

- Runner identity: [self-hosted, Windows, X64, Shoots]
- Job: test
  - Commands:
    - dotnet restore Shoots.sln
    - dotnet build Shoots.sln -c Release --no-restore
    - dotnet test Shoots.sln -c Release --no-build
- Job: determinism-guard
  - Commands:
    - dotnet restore src/Runtime/Shoots.Runtime.sln
    - dotnet build src/Runtime/Shoots.Runtime.sln -c Release --no-restore
    - dotnet test src/Runtime/Shoots.Runtime.sln -c Release --no-build --filter "FullyQualifiedName~Trace_replays_deterministically" (run twice)

## VERIFIED (CI) Criteria

A seal is VERIFIED (CI) only when all conditions are met:

- Both jobs (test, determinism-guard) succeed on the same commit.
- Determinism guard runs the same replay test twice and both passes succeed.
- CI artifacts include test result files (TRX) for both jobs.
- Artifacts are retained per CI configuration.

## Evidence Retention

CI workflows must upload artifacts for both jobs:

- test: solution.trx
- determinism-guard: determinism-pass-1.trx and determinism-pass-2.trx

## CI Visibility Check

Verification requires the workflow run to be visible in GitHub Actions with both jobs present.
Artifacts must be downloadable from the same run as evidence of execution.
Evidence review is a manual GitHub Actions UI step performed by an authorized reviewer.

The seal commit must reference the exact SHA that CI verified.
