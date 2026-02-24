#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
version=$(dotnet msbuild "$repo_root/src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj" -nologo -getProperty:Version)
state_root="$repo_root/.state"
trace_pattern="$state_root/trace/<workorder>.trace.json"
artifacts_pattern="$state_root/artifacts/<workorder>/"
sessions_path="$state_root/chat-intake-sessions.json"
models_path="$state_root/models.catalog.json"
smoke_output_root="$repo_root/artifacts/smoke/local"

mkdir -p "$smoke_output_root"

echo "Repo root: $repo_root"
echo "Version: $version"
echo ".state sessions: $sessions_path"
echo ".state models catalog: $models_path"
echo "Trace pattern: $trace_pattern"
echo "Artifacts pattern: $artifacts_pattern"
echo "Smoke output root: $smoke_output_root"
echo "Tools mode: embedded deterministic execution"

dotnet run --project "$repo_root/src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj" -c Release -- ChatIntakeSmoke | tee "$smoke_output_root/smoke.log"
