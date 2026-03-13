@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-161055961Z-qwen2-5-0-5b-instruct\targets\test-extension"
"dotnet" "test" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-161055961Z-qwen2-5-0-5b-instruct\targets\test-extension\ExtensionCalc.Tests\ExtensionCalc.Tests.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-161055961Z-qwen2-5-0-5b-instruct\targets\test-extension\02-test.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-161055961Z-qwen2-5-0-5b-instruct\targets\test-extension\02-test.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%