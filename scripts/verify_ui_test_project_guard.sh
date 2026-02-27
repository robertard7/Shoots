#!/usr/bin/env bash
set -euo pipefail

project="ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj"

if [[ ! -f "$project" ]]; then
  echo "error: missing $project" >&2
  exit 1
fi

count=$(rg -n "<IsTestProject\\b" "$project" | wc -l | tr -d ' ')
if [[ "$count" != "1" ]]; then
  echo "error: expected exactly one IsTestProject entry in $project, found $count" >&2
  rg -n "<IsTestProject\\b" "$project" >&2 || true
  exit 1
fi

if ! rg -n "<IsTestProject\s+Condition=\"'\\$\\(OS\\)' != 'Windows_NT'\">\s*false\s*</IsTestProject>" "$project" >/dev/null; then
  echo "error: expected IsTestProject guard with Condition \"'\$(OS)' != 'Windows_NT'\" and value false" >&2
  exit 1
fi

echo "ui test project guard passed"
