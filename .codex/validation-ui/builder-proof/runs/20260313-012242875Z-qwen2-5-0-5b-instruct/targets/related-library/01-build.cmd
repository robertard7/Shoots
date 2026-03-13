@echo off
cd /d "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-012242875Z-qwen2-5-0-5b-instruct\targets\related-library"
"dotnet" "build" "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-012242875Z-qwen2-5-0-5b-instruct\targets\related-library\RelatedLibrary.csproj" "-c" "Debug" "-v" "minimal" > "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-012242875Z-qwen2-5-0-5b-instruct\targets\related-library\01-build.log" 2>&1
set SHOOTS_EXITCODE=%errorlevel%
> "C:\dev\Shoots\.codex\validation-ui\builder-proof\runs\20260313-012242875Z-qwen2-5-0-5b-instruct\targets\related-library\01-build.exitcode" echo %SHOOTS_EXITCODE%
exit /b %SHOOTS_EXITCODE%