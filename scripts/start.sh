#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
run_id=$(date -u +%Y%m%d-%H%M%S)
ops_root="$repo_root/artifacts/ops/$run_id"
mkdir -p "$ops_root"

version=$(dotnet msbuild "$repo_root/src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj" -nologo -getProperty:Version)
rev=$(git -C "$repo_root" rev-parse HEAD)

dotnet_ver=$(dotnet --version)
os=$(uname -s)

cat > "$ops_root/env.json" <<JSON
{"os":"$os","dotnet":"$dotnet_ver","repoRev":"$rev","version":"$version","runId":"$run_id"}
JSON

{
  echo "Repo root: $repo_root"
  echo "Version: $version"
  echo "State sessions: $repo_root/.state/chat-intake-sessions.json"
  echo "State models: $repo_root/.state/models.catalog.json"
  echo "Trace pattern: $repo_root/.state/trace/<workorder>.trace.json"
  echo "Artifacts pattern: $repo_root/.state/artifacts/<workorder>/"
  echo "Ops root: $ops_root"
} | tee "$ops_root/start.log"

bash "$repo_root/scripts/verify_provideradapter_naming.sh" | tee -a "$ops_root/start.log"
bash "$repo_root/scripts/verify_versions.sh" | tee -a "$ops_root/start.log"
bash "$repo_root/scripts/verify_repo_topology_guard.sh" | tee -a "$ops_root/start.log"

bash "$repo_root/scripts/run_host_smoke.sh" | tee "$ops_root/smoke.log"
