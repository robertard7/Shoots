#!/usr/bin/env bash
set -euo pipefail

project="ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj"

if [[ ! -f "$project" ]]; then
  echo "error: missing $project" >&2
  exit 1
fi

# Unified search wrapper: prints matching lines with line numbers.
# Usage: _FIND <pattern> <file>
if command -v rg >/dev/null 2>&1; then
  _FIND() { rg -n -S -- "$1" "$2"; }
else
  _FIND() { grep -nE -- "$1" "$2" || true; }
fi

# Pattern 1: any IsTestProject entry (count must be exactly 1)
p_any='<IsTestProject\b'

# Pattern 2: the exact guard entry we require
p_guard='<IsTestProject[[:space:]]+Condition="'"'"'\$\((OS)\)'"'"'[[:space:]]*!=[[:space:]]*'"'"'Windows_NT'"'"'">[[:space:]]*false[[:space:]]*</IsTestProject>'

count="$(_FIND "$p_any" "$project" | wc -l | awk '{print $1}')"
if [[ "$count" != "1" ]]; then
  echo "error: expected exactly one IsTestProject entry in $project, found $count" >&2
  echo "---- matches ----" >&2
  _FIND "$p_any" "$project" >&2 || true
  exit 1
fi

if [[ -z "$(_FIND "$p_guard" "$project")" ]]; then
  echo "error: expected IsTestProject guard with Condition \"'\$(OS)' != 'Windows_NT'\" and value false" >&2
  echo "---- file context ----" >&2
  _FIND "$p_any" "$project" >&2 || true
  exit 1
fi

echo "ui test project guard passed"