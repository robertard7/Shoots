# Compile Fix Loop Summary

- total_iterations: 1
- outcome: blocked-before-windows-gate

## Fixed Failures (in order)

1. Established compile-fix-loop artifact tracking for first-failure and iteration evidence.

## Latest Failure

- phase: environment-sanity
- command: `powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/verify/windows_compile_runtime_integrity.ps1`
- error: `bash: command not found: powershell`

## Final Successful Commands

- none in this environment (Windows gate command unavailable).

## Latest Run Folder

- unavailable

## Expected artifact locations once gate runs on Windows

- `<run-folder>/run.json`
- `<run-folder>/verification_report.json`
- `<run-folder>/operator_flow.json`
- `<run-folder>/transport_equivalence.json`
