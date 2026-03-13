@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140618282Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-test-target"
"dotnet" "test" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140618282Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-test-target\ExternalCalc.Tests\ExternalCalc.Tests.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140618282Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-test-target\02-test.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-140618282Z-qwen2-5-0-5b-instruct\external-target-pack\targets\external-test-target\02-test.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%