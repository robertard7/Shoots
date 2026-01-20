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
- Determinism guard passes twice with identical deterministic outputs.
- CI artifacts include full test logs (console output) and test result files (TRX).
- Artifacts are retained per CI configuration.

## Evidence Retention

CI workflows must upload artifacts for both jobs:

- test: full solution test output
- determinism-guard: both determinism pass outputs

The seal commit must reference the exact SHA that CI verified.
