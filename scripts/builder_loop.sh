#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

ts="$(date +%Y%m%d-%H%M%S)"
out_root="artifacts/builder_loop/${ts}"
mkdir -p "$out_root"

run_and_log() {
  local log="$1"
  shift
  ( "$@" ) 2>&1 | tee "$log"
  return ${PIPESTATUS[0]}
}

run_and_log "$out_root/maintenance.log" bash scripts/maintenance.sh --tests

project_id="builder-smoke"
project_root=".state/projects/${project_id}"
plan_root="${project_root}/plan"
env_root="${project_root}/env"
mkdir -p "$plan_root" "$env_root"

plan_hash="$(printf '%s' "${project_id}|dotnet|Local|host-local|v2" | sha256sum | awk '{print $1}')"
provider_hash="$(printf '%s' "Local|" | sha256sum | awk '{print $1}')"
env_hash="$(printf '%s' "host-local" | sha256sum | awk '{print $1}')"

cat > "${plan_root}/plan.json" <<PLAN
{
  "projectId": "${project_id}",
  "language": "dotnet",
  "providerKind": "Local",
  "environmentId": "host-local",
  "createdAtUtc": "1970-01-01T00:00:00Z",
  "planHash": "${plan_hash}",
  "steps": [
    {
      "stepId": "$(printf '%s' "${plan_hash}|Retrieve" | sha256sum | awk '{print $1}' | cut -c1-12)",
      "kind": "retrieve_context.v1",
      "toolId": "linux.noop.v1",
      "args": {
        "queryText": "builder loop verification",
        "includeGlobs": "src/**/*.cs,docs/*.md",
        "excludeGlobs": "**/bin/**,**/obj/**",
        "maxFiles": 6,
        "maxTotalBytes": 64000,
        "maxFileBytes": 8000
      }
    },
    {
      "stepId": "$(printf '%s' "${plan_hash}|Synthesize" | sha256sum | awk '{print $1}' | cut -c1-12)",
      "kind": "synthesize_plan.v1",
      "toolId": "linux.noop.v1",
      "args": {
        "planKind": "builder_v1"
      }
    },
    {
      "stepId": "$(printf '%s' "${plan_hash}|Write" | sha256sum | awk '{print $1}' | cut -c1-12)",
      "kind": "write_text.v1",
      "toolId": "linux.noop.v1",
      "args": {
        "targetPath": "output/builder-loop.txt",
        "text": "builder loop wrote this file"
      }
    },
    {
      "stepId": "$(printf '%s' "${plan_hash}|Assert" | sha256sum | awk '{print $1}' | cut -c1-12)",
      "kind": "assert_contains.v1",
      "toolId": "linux.noop.v1",
      "args": {
        "targetPath": "output/builder-loop.txt",
        "contains": "builder loop"
      }
    }
  ]
}
PLAN

cat > "${project_root}/provider.json" <<PROVIDER
{
  "kind": "Local",
  "endpoint": "",
  "configHash": "${provider_hash}"
}
PROVIDER

cat > "${env_root}/selected.json" <<ENVSEL
{
  "environmentId": "host-local"
}
ENVSEL

cat > "${env_root}/descriptor.json" <<ENVDESC
{
  "environmentId": "host-local",
  "descriptorHash": "${env_hash}",
  "capabilities": [
    "filesystem",
    "process",
    "verify",
    "retrieval.lexical"
  ]
}
ENVDESC

run_and_log "$out_root/scenario.log" dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$project_root" --out "$out_root/run"

latest_run="$(find "$out_root/run" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
if [[ -z "$latest_run" ]]; then
  echo "No run directory produced by scenario." >&2
  exit 1
fi

run_and_log "$out_root/replay.log" dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- replay --run "$latest_run"

echo "builder_loop artifacts: $out_root"

manifest_path="$out_root/manifest.json"
cat > "$manifest_path" <<MANIFEST
{
  "projectId": "${project_id}",
  "outRoot": "${out_root}",
  "latestRun": "${latest_run}",
  "planHash": "${plan_hash}",
  "providerHash": "${provider_hash}",
  "envHash": "${env_hash}"
}
MANIFEST
