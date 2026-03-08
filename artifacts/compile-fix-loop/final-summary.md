# Compile Fix Loop Summary

- valid_iteration_count: 0
- outcome: blocked-before-windows-gate

## Why blocked

The required Windows integrity gate command could not be executed because this environment is Linux/bash and has no `powershell`/`pwsh` binary.

## Environment evidence

- `uname -a`: Linux
- shell: `/bin/bash`
- `command -v pwsh || command -v powershell`: not found

## Required runner

Use the Windows self-hosted runner:

- labels: `[self-hosted, Windows, X64, Shoots]`

Then run:

```powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/verify/windows_compile_runtime_integrity.ps1
```

(or `pwsh -NoLogo -NoProfile -File tools/verify/windows_compile_runtime_integrity.ps1` on Windows PowerShell 7).

## Current status

- No valid compile-fix loop iteration has started yet.
- No compile/test/smoke/replay phase failure has been captured from a Windows-capable execution.
