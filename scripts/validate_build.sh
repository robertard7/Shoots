#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

bash tools/codex/restore.sh
bash scripts/verify_dotnet_bootstrap.sh

dotnet build Shoots.sln -c Debug -v minimal
dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Debug --filter MainWindowViewModelBackendStatusTests -v minimal
dotnet test Shoots.sln -c Debug -v minimal

echo "validate_build.summary.debug_build=ok"
echo "validate_build.summary.backend_status_tests=ok"
echo "validate_build.summary.solution_tests=ok"
