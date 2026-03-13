@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-050850347Z-qwen2-5-0-5b-instruct\targets\service-sample"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-050850347Z-qwen2-5-0-5b-instruct\targets\service-sample\ProofService.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-050850347Z-qwen2-5-0-5b-instruct\targets\service-sample\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260312-050850347Z-qwen2-5-0-5b-instruct\targets\service-sample\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%