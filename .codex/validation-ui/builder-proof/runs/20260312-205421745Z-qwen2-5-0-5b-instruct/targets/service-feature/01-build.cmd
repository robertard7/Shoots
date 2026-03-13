@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-205421745Z-qwen2-5-0-5b-instruct\targets\service-feature"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-205421745Z-qwen2-5-0-5b-instruct\targets\service-feature\ProofFeatureService.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-205421745Z-qwen2-5-0-5b-instruct\targets\service-feature\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-205421745Z-qwen2-5-0-5b-instruct\targets\service-feature\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%