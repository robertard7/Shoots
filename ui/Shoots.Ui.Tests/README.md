# Shoots.Ui.Tests

These tests target WPF/Desktop components and need Windows Desktop SDK targets.

## Why Windows-only
`Shoots.Ui` depends on `Microsoft.NET.Sdk.WindowsDesktop`, which is unavailable on Linux CI images used for runtime/host jobs.

## Run locally on Windows
```powershell
dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release -m:1
```

## Prerequisites
- Windows 10/11
- .NET SDK 8.x with WindowsDesktop targets installed
- Repository root as working directory
