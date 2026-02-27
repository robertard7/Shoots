First-Run Checklist
===================

Checklist
---------
- [ ] Startup tab is visible on first run.
- [ ] "New Project" starts the startup flow and prompts for an entry path.
- [ ] "Start something new" runs language → name → description → confirm.
- [ ] Summary block includes Provider, Mode, Language, Project name, Sandbox path, Files.
- [ ] No files are created until "confirm".
- [ ] README.intent.md is created after confirm and is reported in chat.
- [ ] "Continue an existing project" accepts a folder and warns if outside sandbox.
- [ ] Attach flow reports read-only status and performs no writes.
- [ ] "Just explore an idea" shows "Explore (no writes)" and does not write to disk.
- [ ] "promote" restarts the startup flow without writing files.
- [ ] Startup tab is disabled once a project is active.
- [ ] "Start another project" re-enables startup and resets flow.

Sign-Off
--------
- [ ] Completed by: ______________________
- [ ] Date: ______________________________

Green Build Recipe
------------------
Windows (self-hosted runner parity)
- `dotnet restore Shoots.sln`
- `dotnet build -c Release Shoots.sln -p:ContinuousIntegrationBuild=true`
- `dotnet test -c Release Shoots.sln -p:ContinuousIntegrationBuild=true`
- UI-only verification: `dotnet test -c Release ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -p:ContinuousIntegrationBuild=true`

Linux parity
- `dotnet restore src/Runtime/Shoots.Runtime.sln`
- `dotnet build src/Runtime/Shoots.Runtime.sln -c Release -p:ContinuousIntegrationBuild=true`
- `dotnet test src/Runtime/Shoots.Runtime.sln -c Release -p:ContinuousIntegrationBuild=true`
- `dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -p:ContinuousIntegrationBuild=true`
- `dotnet test src/Builder/Shoots.Builder.Tests/Shoots.Builder.Tests.csproj -c Release -p:ContinuousIntegrationBuild=true`

Notes
- `ui/Shoots.Ui.Tests` is Windows-only by design (`net8.0-windows`).
- Optional CI failure capture: `bash scripts/collect_ci_first_failure.sh <branch>`
- Keep `IsTestProject` guarded on non-Windows: `<IsTestProject Condition="'$(OS)' != 'Windows_NT'">false</IsTestProject>`.

