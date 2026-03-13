@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140702411Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-console-app"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140702411Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-console-app\ExternalConsole.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140702411Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-console-app\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140702411Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-console-app\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%