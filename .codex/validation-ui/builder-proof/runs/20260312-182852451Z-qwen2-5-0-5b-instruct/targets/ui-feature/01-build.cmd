@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-182852451Z-qwen2-5-0-5b-instruct\targets\ui-feature"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-182852451Z-qwen2-5-0-5b-instruct\targets\ui-feature\WpfProof.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-182852451Z-qwen2-5-0-5b-instruct\targets\ui-feature\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-182852451Z-qwen2-5-0-5b-instruct\targets\ui-feature\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%