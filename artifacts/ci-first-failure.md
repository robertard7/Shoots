# First failure capture

## `dotnet --info`
- Failed: `dotnet: command not found`

## `dotnet build Shoots.sln -c Debug -v minimal`
- Not executed because `dotnet` CLI is unavailable in this execution environment.

## `dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Debug -v minimal`
- Not executed because `dotnet` CLI is unavailable in this execution environment.
