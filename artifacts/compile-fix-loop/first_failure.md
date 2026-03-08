# First Failure (Windows gate not started)

- valid_iteration: false
- phase: environment-mismatch
- command_attempted: `powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/verify/windows_compile_runtime_integrity.ps1`
- type: runner-environment-mismatch
- file: n/a
- line: n/a

## Environment detection

- `uname -a`: Linux kernel/runtime detected
- `command -v pwsh || command -v powershell`: not found
- shell: `/bin/bash`

## Error

```text
bash: command not found: powershell
```

## Required next step

Run the compile-fix loop on the Windows self-hosted runner (`[self-hosted, Windows, X64, Shoots]`) using:

```powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File tools/verify/windows_compile_runtime_integrity.ps1
```

(or `pwsh` on Windows if only PowerShell 7 is available).
