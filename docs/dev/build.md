# Build and test (Debug/Release)

## Bootstrap .NET SDK

```bash
bash tools/codex/restore.sh
bash scripts/verify_dotnet_bootstrap.sh
```

The required SDK version is read from `global.json` (currently `8.0.418`).

## Debug

```bash
dotnet build Shoots.sln -c Debug -v minimal
dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Debug --filter MainWindowViewModelBackendStatusTests -v minimal
dotnet test Shoots.sln -c Debug -v minimal
```

## Release

```bash
dotnet build Shoots.sln -c Release -v minimal
dotnet test Shoots.sln -c Release -v minimal
```

## One-command validation

```bash
bash scripts/validate_build.sh
```

## No-warnings gate

```bash
bash scripts/validate_build.sh --warnings-as-errors
bash scripts/verify_no_warnings.sh
```
