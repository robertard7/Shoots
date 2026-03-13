@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-022043609Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-multi-file-console"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-022043609Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-multi-file-console\ExternalMultiConsole.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-022043609Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-multi-file-console\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-022043609Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-multi-file-console\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%